//@ts-ignore
import * as modlib from "modlib";

// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.

/**
 * Minimal RuntimeMigrator loader for Battlefield Portal.
 *
 * Upload this file as the Portal runtime script and upload the generated
 * `<output>.strings.json` as the experience strings file. The script decodes
 * the RuntimeMigrator binary stream and spawns every encoded object once.
 */

const STRING_CHUNK_SIZE = 200;
const DECODED_CHUNK_SIZE = STRING_CHUNK_SIZE / 2;
const STREAM_PREFIX = "A";

const FORMAT_VERSION = 1;
const MAX_UINT16 = 65535;
const SCALE_MAX = 100.0;
const ROTATION_RANGE = Math.PI * 2;
const ROTATION_OFFSET = Math.PI;
const CUSTOM_PALETTE_SENTINEL = 255;

const CUSTOM_BASE16_TABLE = "_-,.:`~'+=^%<}] ";
const CUSTOM_BASE16_MAP = new Array<number>(128).fill(-1);

for (let i = 0; i < CUSTOM_BASE16_TABLE.length; i++) {
    CUSTOM_BASE16_MAP[CUSTOM_BASE16_TABLE.charCodeAt(i)] = i;
}

function decodeCustomBase16(data: string, output: Uint8Array): number {
    if ((data.length & 1) !== 0) throw new Error("Hex string length must be even.");

    const decodedLength = data.length / 2;
    if (output.length < decodedLength) throw new Error("Data buffer too small for decoded output.");

    for (let i = 0, j = 0; i < data.length; i += 2, j++) {
        const hiCode = data.charCodeAt(i);
        const loCode = data.charCodeAt(i + 1);
        const hi = hiCode < CUSTOM_BASE16_MAP.length ? CUSTOM_BASE16_MAP[hiCode]! : -1;
        const lo = loCode < CUSTOM_BASE16_MAP.length ? CUSTOM_BASE16_MAP[loCode]! : -1;

        if (hi < 0 || lo < 0) throw new Error(`Invalid custom base16 character at index ${i}.`);

        output[j] = (hi << 4) | lo;
    }

    return decodedLength;
}

class RuntimeVector {
    constructor(public x: number, public y: number, public z: number) {}

    public toModVector(): mod.Vector {
        return mod.CreateVector(this.x, this.y, this.z);
    }
}

class RuntimeStringsStream {
    private readonly strings: { [key: string]: string };
    private readonly decodedChunk = new Uint8Array(DECODED_CHUNK_SIZE);
    private chunkIndex = 0;
    private chunkOffset = 0;
    private chunkLength = 0;
    private initialized = false;
    private eof = false;

    constructor(private readonly prefix: string = STREAM_PREFIX) {
        this.strings = (mod as any).strings;
        if (typeof this.strings !== "object" || this.strings === null) {
            throw new Error("Runtime object 'mod.strings' is not available.");
        }
    }

    public read(byteCount: number): Uint8Array | null {
        const result = new Uint8Array(byteCount);
        let copied = 0;

        while (copied < byteCount) {
            if (!this.initialized || this.chunkOffset >= this.chunkLength) {
                if (!this.loadNextChunk()) {
                    return copied > 0 ? result.subarray(0, copied) : null;
                }
            }

            const available = Math.min(byteCount - copied, this.chunkLength - this.chunkOffset);
            result.set(this.decodedChunk.subarray(this.chunkOffset, this.chunkOffset + available), copied);
            this.chunkOffset += available;
            copied += available;
        }

        return result;
    }

    private loadNextChunk(): boolean {
        if (this.eof) return false;

        const key = `${this.prefix}${this.chunkIndex.toString(16).toUpperCase()}`;
        const encoded = this.strings[key];
        if (encoded === undefined) {
            this.eof = true;
            return false;
        }

        this.chunkLength = decodeCustomBase16(encoded, this.decodedChunk);
        this.chunkOffset = 0;
        this.chunkIndex++;
        this.initialized = true;
        return true;
    }
}

class BinaryStreamReader {
    private static readonly BUFFER_SIZE = DECODED_CHUNK_SIZE;

    private readonly buffer = new Uint8Array(BinaryStreamReader.BUFFER_SIZE);
    private readonly view = new DataView(this.buffer.buffer);
    private readonly stringBuffer = new Uint8Array(512);
    private bufferOffset = 0;
    private bufferLength = 0;
    private sourceEof = false;

    public totalOffset = 0;

    constructor(private readonly source: RuntimeStringsStream) {}

    public readByte(): number {
        return this.view.getUint8(this.readRaw(1));
    }

    public readUInt16(): number {
        return this.view.getUint16(this.readRaw(2), true);
    }

    public readInt16(): number {
        return this.view.getInt16(this.readRaw(2), true);
    }

    public readUInt32(): number {
        return this.view.getUint32(this.readRaw(4), true);
    }

    public readInt32(): number {
        return this.view.getInt32(this.readRaw(4), true);
    }

    public readFloat32(): number {
        return this.view.getFloat32(this.readRaw(4), true);
    }

    public readFloatVector(): RuntimeVector {
        return new RuntimeVector(this.readFloat32(), this.readFloat32(), this.readFloat32());
    }

    public readString(): string {
        let byteCount = 0;
        let shift = 0;
        let more = false;
        do {
            const b = this.readByte();
            byteCount |= (b & 0x7f) << shift;
            shift += 7;
            more = (b & 0x80) !== 0;
        } while (more);

        if (byteCount === 0) return "";

        if (byteCount <= this.bufferLength - this.bufferOffset) {
            const value = decodeUtf8(this.buffer, this.bufferOffset, byteCount);
            this.bufferOffset += byteCount;
            this.totalOffset += byteCount;
            return value;
        }

        if (byteCount > this.stringBuffer.length) {
            throw new Error(`String length ${byteCount} exceeds fixed string buffer size ${this.stringBuffer.length}.`);
        }

        let copied = 0;
        while (copied < byteCount) {
            this.ensureData(1);
            const available = Math.min(byteCount - copied, this.bufferLength - this.bufferOffset);
            this.stringBuffer.set(this.buffer.subarray(this.bufferOffset, this.bufferOffset + available), copied);
            this.bufferOffset += available;
            this.totalOffset += available;
            copied += available;
        }

        return decodeUtf8(this.stringBuffer, 0, byteCount);
    }

    private readRaw(size: number): number {
        this.ensureData(size);
        const offset = this.bufferOffset;
        this.bufferOffset += size;
        this.totalOffset += size;
        return offset;
    }

    private ensureData(size: number): void {
        if (this.bufferLength - this.bufferOffset >= size) return;

        if (this.bufferOffset > 0 && this.bufferOffset < this.bufferLength) {
            const remaining = this.bufferLength - this.bufferOffset;
            this.buffer.copyWithin(0, this.bufferOffset, this.bufferLength);
            this.bufferLength = remaining;
            this.bufferOffset = 0;
        } else if (this.bufferOffset >= this.bufferLength) {
            this.bufferOffset = 0;
            this.bufferLength = 0;
        }

        while (this.bufferLength - this.bufferOffset < size && !this.sourceEof) {
            const spaceLeft = BinaryStreamReader.BUFFER_SIZE - this.bufferLength;
            if (spaceLeft <= 0) break;

            const chunk = this.source.read(spaceLeft);
            if (chunk && chunk.length > 0) {
                this.buffer.set(chunk, this.bufferLength);
                this.bufferLength += chunk.length;
            } else {
                this.sourceEof = true;
            }
        }

        if (this.bufferLength - this.bufferOffset < size) {
            throw new Error(`Unexpected EOF: need ${size}, have ${this.bufferLength - this.bufferOffset}.`);
        }
    }
}

function decodeUtf8(bytes: Uint8Array, start: number, length: number): string {
    let result = "";
    const end = start + length;

    for (let i = start; i < end; ) {
        const first = bytes[i++];
        if (first < 0x80) {
            result += String.fromCharCode(first);
        } else if (first < 0xe0) {
            if (i >= end) throw new Error("Incomplete 2-byte UTF-8 sequence.");
            const second = bytes[i++];
            result += String.fromCharCode(((first & 0x1f) << 6) | (second & 0x3f));
        } else if (first < 0xf0) {
            if (i + 1 >= end) throw new Error("Incomplete 3-byte UTF-8 sequence.");
            const second = bytes[i++];
            const third = bytes[i++];
            result += String.fromCharCode(((first & 0x0f) << 12) | ((second & 0x3f) << 6) | (third & 0x3f));
        } else {
            if (i + 2 >= end) throw new Error("Incomplete 4-byte UTF-8 sequence.");
            const second = bytes[i++];
            const third = bytes[i++];
            const fourth = bytes[i++];
            const codePoint = ((first & 0x07) << 18) | ((second & 0x3f) << 12) | ((third & 0x3f) << 6) | (fourth & 0x3f);
            const adjusted = codePoint - 0x10000;
            result += String.fromCharCode(0xd800 + (adjusted >> 10), 0xdc00 + (adjusted & 0x3ff));
        }
    }

    return result;
}

type RuntimeSpawnEnum = { [key: string]: number };

const mapSpecificEnums: { [key: string]: RuntimeSpawnEnum | undefined } = {
    Abbasid: mod.RuntimeSpawn_Abbasid as any,
    Aftermath: mod.RuntimeSpawn_Aftermath as any,
    Badlands: mod.RuntimeSpawn_Badlands as any,
    Battery: mod.RuntimeSpawn_Battery as any,
    Capstone: mod.RuntimeSpawn_Capstone as any,
    Contaminated: mod.RuntimeSpawn_Contaminated as any,
    Dumbo: mod.RuntimeSpawn_Dumbo as any,
    Eastwood: mod.RuntimeSpawn_Eastwood as any,
    FireStorm: mod.RuntimeSpawn_FireStorm as any,
    Limestone: mod.RuntimeSpawn_Limestone as any,
    Outskirts: mod.RuntimeSpawn_Outskirts as any,
    Sand: mod.RuntimeSpawn_Sand as any,
    Subsurface: mod.RuntimeSpawn_Subsurface as any,
    Tungsten: mod.RuntimeSpawn_Tungsten as any,
    Granite_Downtown: mod.RuntimeSpawn_Granite_Downtown as any,
    Granite_Marina: mod.RuntimeSpawn_Granite_Marina as any,
    Granite_MilitaryRnD: mod.RuntimeSpawn_Granite_MilitaryRnD as any,
    Granite_MilitaryStorage: mod.RuntimeSpawn_Granite_MilitaryStorage as any,
    Granite_ResidentialNorth: mod.RuntimeSpawn_Granite_ResidentialNorth as any,
    Granite_TechCenter: mod.RuntimeSpawn_Granite_TechCenter as any,
    Granite_Underground: mod.RuntimeSpawn_Granite_Underground as any,
    Granite_TechCampus_Portal: (mod as any)["RuntimeSpawn_Granite_TechCampus_Portal"] ?? (mod.RuntimeSpawn_Granite_TechCenter as any),
    Granite: (mod as any)["RuntimeSpawn_Granite_TechCampus_Portal"] ?? (mod.RuntimeSpawn_Granite_TechCenter as any),
};

interface ChunkInfo {
    cx: number;
    cy: number;
    cz: number;
    offset: number;
    length: number;
}

class StaticSpatialSpawner {
    private readonly reader = new BinaryStreamReader(new RuntimeStringsStream());
    private readonly scalePalette: RuntimeVector[];
    private readonly rotationPalette: RuntimeVector[];
    private readonly typePalette: string[];
    private readonly chunks: ChunkInfo[];
    private readonly chunkSize: number;
    private readonly mapEnum: RuntimeSpawnEnum | undefined;

    private spawnedCount = 0;
    private skippedCount = 0;

    constructor() {
        const version = this.reader.readUInt16();
        if (version !== FORMAT_VERSION) throw new Error(`Unsupported binary version: ${version}.`);

        this.chunkSize = this.reader.readFloat32();
        const mapType = this.reader.readString();
        const encodedObjectCount = this.reader.readInt32();
        const chunkCount = this.reader.readUInt16();

        // Bounds are included for metadata/preallocation. The minimal static spawner does not need them.
        this.reader.readFloatVector();
        this.reader.readFloatVector();

        const scalePaletteCount = this.reader.readUInt16();
        const rotationPaletteCount = this.reader.readUInt16();
        const typePaletteCount = this.reader.readUInt16();

        this.scalePalette = new Array<RuntimeVector>(scalePaletteCount);
        this.rotationPalette = new Array<RuntimeVector>(rotationPaletteCount);
        this.typePalette = new Array<string>(typePaletteCount);
        this.chunks = new Array<ChunkInfo>(chunkCount);
        this.mapEnum = mapSpecificEnums[mapType];

        for (let i = 0; i < scalePaletteCount; i++) this.scalePalette[i] = this.reader.readFloatVector();
        for (let i = 0; i < rotationPaletteCount; i++) this.rotationPalette[i] = this.reader.readFloatVector();
        for (let i = 0; i < typePaletteCount; i++) this.typePalette[i] = this.reader.readString();

        for (let i = 0; i < chunkCount; i++) {
            this.chunks[i] = {
                cx: this.reader.readInt16(),
                cy: this.reader.readInt16(),
                cz: this.reader.readInt16(),
                offset: this.reader.readUInt32(),
                length: this.reader.readUInt32(),
            };
        }

        console.log(`RuntimeMigrator loaded map=${mapType}, encodedObjects=${encodedObjectCount}, chunks=${chunkCount}.`);
    }

    public spawnAll(): void {
        for (const chunk of this.chunks) {
            this.spawnChunk(chunk);
        }

        console.log(`RuntimeMigrator static spawn complete. spawned=${this.spawnedCount}, skipped=${this.skippedCount}.`);
    }

    private spawnChunk(chunk: ChunkInfo): void {
        if (this.reader.totalOffset !== chunk.offset) {
            throw new Error(
                `Chunk offset mismatch for [${chunk.cx},${chunk.cy},${chunk.cz}]: expected ${chunk.offset}, actual ${this.reader.totalOffset}.`
            );
        }

        const origin = new RuntimeVector(chunk.cx * this.chunkSize, chunk.cy * this.chunkSize, chunk.cz * this.chunkSize);
        const objectCount = this.reader.readUInt16();

        for (let i = 0; i < objectCount; i++) {
            this.spawnNextObject(origin);
        }
    }

    private spawnNextObject(origin: RuntimeVector): void {
        const pos = new RuntimeVector(
            origin.x + (this.reader.readUInt16() / MAX_UINT16) * this.chunkSize,
            origin.y + (this.reader.readUInt16() / MAX_UINT16) * this.chunkSize,
            origin.z + (this.reader.readUInt16() / MAX_UINT16) * this.chunkSize
        );

        const scaleIndex = this.reader.readByte();
        const rotationIndex = this.reader.readByte();
        const typeIndex = this.reader.readUInt16();

        const scale =
            scaleIndex === CUSTOM_PALETTE_SENTINEL
                ? new RuntimeVector(
                      (this.reader.readUInt16() / MAX_UINT16) * SCALE_MAX,
                      (this.reader.readUInt16() / MAX_UINT16) * SCALE_MAX,
                      (this.reader.readUInt16() / MAX_UINT16) * SCALE_MAX
                  )
                : this.scalePalette[scaleIndex];

        const rotation =
            rotationIndex === CUSTOM_PALETTE_SENTINEL
                ? new RuntimeVector(
                      (this.reader.readUInt16() / MAX_UINT16) * ROTATION_RANGE - ROTATION_OFFSET,
                      (this.reader.readUInt16() / MAX_UINT16) * ROTATION_RANGE - ROTATION_OFFSET,
                      (this.reader.readUInt16() / MAX_UINT16) * ROTATION_RANGE - ROTATION_OFFSET
                  )
                : this.rotationPalette[rotationIndex];

        const typeName = this.typePalette[typeIndex];
        const prefab = this.resolvePrefab(typeName);
        if (prefab === undefined || scale === undefined || rotation === undefined) {
            console.warn(`Skipping unknown or invalid object type='${typeName}' scaleIndex=${scaleIndex} rotationIndex=${rotationIndex}.`);
            this.skippedCount++;
            return;
        }

        mod.SpawnObject(prefab as any, pos.toModVector(), rotation.toModVector(), scale.toModVector());
        this.spawnedCount++;
    }

    private resolvePrefab(typeName: string): number | undefined {
        if (this.mapEnum && this.mapEnum[typeName] !== undefined) return this.mapEnum[typeName];
        return (mod.RuntimeSpawn_Common as any)[typeName];
    }
}

export function OnGameModeStarted(): void {
    try {
        const spawner = new StaticSpatialSpawner();
        spawner.spawnAll();
    } catch (error) {
        console.error(`RuntimeMigrator static spawner failed: ${error}`);
    }
}
