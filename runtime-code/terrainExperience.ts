//@ts-ignore
import * as modlib from "modlib";

// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
const STR_CHUNK_SIZE = 200; // Number of encoded characters per string chunk
const DECODE_CHUNK_SIZE = STR_CHUNK_SIZE / 2; // Number of decoded bytes per character chunk

const CUSTOM_BASE16_TABLE = "_-,.:`~'+=^%<}] ";
const CUSTOM_BASE16_MAP = new Array<number>(128).fill(-1);

// Precompute the base16 character to value map
// Index: 0-15 mapped to characters in CUSTOM_BASE16_TABLE
// All invalid characters map to -1
for (let i = 0; i < 16; i++) {
    const charCode = CUSTOM_BASE16_TABLE.charCodeAt(i);
    CUSTOM_BASE16_MAP[charCode] = i;
}

function decodeCustomBase16(data: string, outData: Uint8Array): number {
    const remainder = data.length % 2;
    if (remainder !== 0) throw new Error("Hex string length must be even.");

    const decodedLength = data.length / 2;

    if (outData.length < decodedLength) throw new Error("Data buffer too small for decoded output.");

    for (let i = 0, j = 0; i < data.length; i += 2, j++) {
        const hiVal = data.charCodeAt(i);
        const lowVal = data.charCodeAt(i + 1);
        const hi = hiVal < 128 ? CUSTOM_BASE16_MAP[hiVal]! : -1;
        const lo = lowVal < 128 ? CUSTOM_BASE16_MAP[lowVal]! : -1;

        if (hi === -1 || lo === -1) throw new Error(`Invalid hex character at index ${i}.`);

        outData[j] = (hi << 4) | lo;
    }
    return decodedLength;
}

// This class reads binary data stored in the runtime strings object in chunks.
// It decodes the custom base16 encoding used to store binary data in strings.
// Memory usage is minimized by reusing a fixed size buffer for chunk decoding.
// since the current limitation overall is runtime memory usage
class SpatialSteamReader {
    private readonly strings: { [key: string]: string };
    private chunkIndex = 0;
    private initialized = false;
    private chunkData: Uint8Array = new Uint8Array(DECODE_CHUNK_SIZE);
    private currentChunkLength: number = 0;
    private chunkOffset = 0;
    private streamPrefix: string;
    public eof = false;

    constructor(steamPrefix: string = "A") {
        this.strings = (mod as any).strings;
        if (typeof this.strings !== "object" || this.strings === null)
            throw new Error("Runtime object 'mod.strings' is not available.");
        this.streamPrefix = steamPrefix;
    }

    private updateDataChunk(): boolean {
        if (this.eof) return false;

        const key = `${this.streamPrefix}${this.chunkIndex.toString(16).toUpperCase()}`;
        const chunkStr = this.strings[key];

        if (chunkStr === undefined) {
            this.eof = true;
            console.log(`EOF reached at chunk index ${this.chunkIndex}`);
            return true;
        }

        this.currentChunkLength = decodeCustomBase16(chunkStr, this.chunkData);
        this.chunkOffset = 0;
        this.chunkIndex++;
        this.initialized = true;
        return false;
    }

    public read(byteCount: number): Uint8Array | null {
        if (this.eof && (this.chunkData === null || this.chunkOffset >= this.chunkData.length)) {
            return null;
        }

        const resultBuffer = new Uint8Array(byteCount);
        let bytesCopied = 0;

        while (bytesCopied < byteCount) {
            if (!this.initialized || this.chunkOffset >= this.currentChunkLength) {
                const eof = this.updateDataChunk();
                if (eof) {
                    return bytesCopied > 0 ? resultBuffer.subarray(0, bytesCopied) : null;
                }
            }

            const chunk = this.chunkData!;
            const bytesToCopy = Math.min(byteCount - bytesCopied, this.currentChunkLength - this.chunkOffset);

            resultBuffer.set(chunk.subarray(this.chunkOffset, this.chunkOffset + bytesToCopy), bytesCopied);

            this.chunkOffset += bytesToCopy;
            bytesCopied += bytesToCopy;
        }
        return resultBuffer;
    }
}

// Wrapper around DataView and RuntimeStringsReader to provide binary reading capabilities from mod.stringkeys
class AsyncBinaryReader {
    private buffer: Uint8Array;
    private view: DataView;
    private bufOffset: number = 0;
    private bufLength: number = 0;
    public totalOffset: number = 0;
    private stringReader: SpatialSteamReader;
    private _isStringReaderEof: boolean = false;

    private static readonly BUFFER_SIZE = 100;
    private tempStringBuffer: Uint8Array = new Uint8Array(0);

    constructor(reader: SpatialSteamReader) {
        this.stringReader = reader;
        this.buffer = new Uint8Array(AsyncBinaryReader.BUFFER_SIZE);
        this.view = new DataView(this.buffer.buffer);
    }

    private ensureData(byteCount: number): void {
        if (this.bufLength - this.bufOffset >= byteCount) return;

        if (this.bufOffset > 0 && this.bufOffset < this.bufLength) {
            const remaining = this.bufLength - this.bufOffset;
            this.buffer.copyWithin(0, this.bufOffset, this.bufLength);
            this.bufLength = remaining;
            this.bufOffset = 0;
        } else if (this.bufOffset >= this.bufLength) {
            this.bufOffset = 0;
            this.bufLength = 0;
        }

        while (this.bufLength - this.bufOffset < byteCount && !this._isStringReaderEof) {
            const spaceLeft = AsyncBinaryReader.BUFFER_SIZE - this.bufLength;
            if (spaceLeft <= 0) break;

            const chunk = this.stringReader.read(spaceLeft);
            if (chunk && chunk.length > 0) {
                this.buffer.set(chunk, this.bufLength);
                this.bufLength += chunk.length;
            } else {
                this._isStringReaderEof = true;
                break;
            }
        }

        if (this.bufLength - this.bufOffset < byteCount) {
            throw new Error(`Unexpected EOF: need ${byteCount}, have ${this.bufLength - this.bufOffset}`);
        }
    }

    private readRaw(size: number): number {
        this.ensureData(size);
        const off = this.bufOffset;
        this.bufOffset += size;
        this.totalOffset += size;
        return off;
    }

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

    public readFloatVector(): Vector {
        return new Vector(this.readFloat32(), this.readFloat32(), this.readFloat32());
    }

    public readString(): string {
        // To read the C# style string we need to first parse the variable length integer length
        let byteCount = 0;
        let shift = 0;
        let cont: boolean;
        do {
            const b = this.readByte();
            byteCount |= (b & 0x7f) << shift;
            shift += 7;
            cont = (b & 0x80) !== 0;
        } while (cont);

        // If the string does not cross buffer boundies we can load it with zero copies
        if (this.bufLength - this.bufOffset >= byteCount) {
            const strRtn = decodeUTF8(this.buffer, this.bufOffset, byteCount);
            this.bufOffset += byteCount;
            this.totalOffset += byteCount;
            return strRtn;
        }

        // Reallocate the the string temp buffer if too small
        if (this.tempStringBuffer.length < byteCount) this.tempStringBuffer = new Uint8Array(byteCount);

        let copied = 0;
        while (copied < byteCount) {
            this.ensureData(1);
            const available = Math.min(byteCount - copied, this.bufLength - this.bufOffset);
            this.tempStringBuffer.set(this.buffer.subarray(this.bufOffset, this.bufOffset + available), copied);
            this.bufOffset += available;
            this.totalOffset += available;
            copied += available;
        }

        return decodeUTF8(this.tempStringBuffer, 0, byteCount);
    }
}

// Decodes a UTF-8 encoded byte array into a string
function decodeUTF8(bytes: Uint8Array, start: number, length: number): string {
    const chars: number[] = []; // Store code points as numbers
    const endPos = start + length;

    if (endPos > bytes.length) {
        throw new Error("Decode length goes past byte array length");
    }

    let i = start;
    while (i < endPos) {
        let c = bytes[i++];
        if (c < 128) {
            chars.push(c);
        } else if (c > 191 && c < 224) {
            if (i >= endPos) throw new Error("Incomplete 2-byte UTF-8 sequence");
            const c2 = bytes[i++];
            chars.push(((c & 31) << 6) | (c2 & 63));
        } else if (c > 223 && c < 240) {
            if (i + 1 >= endPos) throw new Error("Incomplete 3-byte UTF-8 sequence");
            const c2 = bytes[i++];
            const c3 = bytes[i++];
            chars.push(((c & 15) << 12) | ((c2 & 63) << 6) | (c3 & 63));
        } else {
            if (i + 2 >= endPos) throw new Error("Incomplete 4-byte UTF-8 sequence");
            const c2 = bytes[i++];
            const c3 = bytes[i++];
            const c4 = bytes[i++];
            const codePoint = ((c & 0x07) << 18) | ((c2 & 0x3f) << 12) | ((c3 & 0x3f) << 6) | (c4 & 0x3f);
            if (codePoint < 0x10000) {
                chars.push(codePoint);
            } else {
                // Surrogate pair
                chars.push(0xd800 + ((codePoint - 0x10000) >> 10), 0xdc00 + ((codePoint - 0x10000) & 0x3ff));
            }
        }
    }

    return String.fromCharCode(...chars);
}

// TODO - Currently missing the mp_granite map enum that are also missing from the SDK
const mapTypeToEnum: { [key: string]: any } = {
    Abbasid: mod.RuntimeSpawn_Abbasid,
    Aftermath: mod.RuntimeSpawn_Aftermath,
    Badlands: mod.RuntimeSpawn_Badlands,
    Battery: mod.RuntimeSpawn_Battery,
    Capstone: mod.RuntimeSpawn_Capstone,
    Dumbo: mod.RuntimeSpawn_Dumbo,
    FireStorm: mod.RuntimeSpawn_FireStorm,
    Limestone: mod.RuntimeSpawn_Limestone,
    Outskirts: mod.RuntimeSpawn_Outskirts,
    Tungsten: mod.RuntimeSpawn_Tungsten,
    Granite_TechCampus_Portal: (mod as any)["RuntimeSpawn_Granite_TechCampus_Portal"],
};

class Vector {
    constructor(public x: number = 0, public y: number = 0, public z: number = 0) {}

    public toModVector(): mod.Vector {
        return mod.CreateVector(this.x, this.y, this.z);
    }

    public fromModVector(vec: mod.Vector): Vector {
        this.x = mod.XComponentOf(vec);
        this.y = mod.YComponentOf(vec);
        this.z = mod.ZComponentOf(vec);
        return this;
    }
}

const ZeroVector = new Vector(0, 0, 0);
const ModZeroVector = mod.CreateVector(0, 0, 0);
const ModOneVector = mod.CreateVector(1, 1, 1);

interface SpatialObject {
    uid: number;
    typeId: number;
    position: Vector; // Used for spatial distance calculations during object capture
    // Pre-computed mod.Vector instances for efficient spawning (avoids repeated conversions)
    modPosition: mod.Vector;
    modScale: mod.Vector;
    modRotation: mod.Vector;
}

interface ChunkInfo {
    cx: number;
    cy: number;
    cz: number;
    offset: number;
    length: number;
}

interface ObjectSpan {
    start: number;
    end: number;
}

class MapObjectData {
    public readonly chunkObjects: Array<SpatialObject>;
    public readonly chunkSegments: Map<string, ObjectSpan>;
    public readonly chunkSize: number;
    public readonly objectCount: number;
    public readonly uidToChunk: Map<number, string>;

    constructor(
        chunkObjects: Array<SpatialObject>,
        chunkSegments: Map<string, ObjectSpan>,
        chunkSize: number,
        uidToChunk: Map<number, string>
    ) {
        this.chunkObjects = chunkObjects;
        this.chunkSegments = chunkSegments;
        this.chunkSize = chunkSize;
        this.objectCount = chunkObjects.length;
        this.uidToChunk = uidToChunk;
    }
}

/**
 * Manages incremental parsing of binary data.
 * Processes a fixed number of chunks per update cycle to avoid loop execution limits.
 */
class IncrementalDataParser {
    private reader: AsyncBinaryReader;
    private chunkInfos: ChunkInfo[];
    private scalePalette: Vector[];
    private rotationPalette: Vector[];
    private typePalette: string[];
    private chunkObjects: SpatialObject[];
    private chunkMap: Map<string, ObjectSpan> = new Map();
    private uidToChunk: Map<number, string> = new Map();
    private chunkSize: number = 0;
    private mapSpecificEnum: any = null;

    private headerParsed = false;
    private currentChunkIndex = 0;
    private currentObjectCount = 0;
    private isComplete = false;

    // Not currently used but may be useful for bounds checking
    private chunksMinBounds: Vector = ZeroVector;
    private chunksMaxBounds: Vector = ZeroVector;

    private readonly CHUNKS_PER_CYCLE = 2;
    private readonly MaxUint16 = 65535.0;
    private readonly ScaleMax = 100.0;
    private readonly RotationRange = Math.PI * 2;
    private readonly RotationOffset = Math.PI;

    constructor() {
        this.reader = new AsyncBinaryReader(new SpatialSteamReader());

        console.log("Parsing stream header and palettes...");

        // Parse the header and palette data
        const version = this.reader.readUInt16();
        if (version !== 1) throw new Error(`Unsupported binary version: ${version}.`);

        // Read all header fields
        this.chunkSize = this.reader.readFloat32();
        const mapType = this.reader.readString();
        this.mapSpecificEnum = mapTypeToEnum[mapType];

        // All object counts are read first so we can preallocate arrays
        const objectCount = this.reader.readInt32();
        const chunkCount = this.reader.readUInt16();

        this.chunksMinBounds = this.reader.readFloatVector();
        this.chunksMaxBounds = this.reader.readFloatVector();

        const scalePaletteCount = this.reader.readUInt16();
        const rotationPaletteCount = this.reader.readUInt16();

        const typePaletteCount = this.reader.readUInt16();

        this.chunkObjects = new Array<SpatialObject>(objectCount);
        this.scalePalette = new Array<Vector>(scalePaletteCount);
        this.rotationPalette = new Array<Vector>(rotationPaletteCount);
        this.typePalette = new Array<string>(typePaletteCount);
        this.chunkInfos = new Array<ChunkInfo>(chunkCount);
    }

    public parseDataPalette(): void {
        if (this.headerParsed) return;

        for (let i = 0; i < this.scalePalette.length; i++) {
            this.scalePalette[i] = this.reader.readFloatVector();
        }

        for (let i = 0; i < this.rotationPalette.length; i++) {
            this.rotationPalette[i] = this.reader.readFloatVector();
        }

        for (let i = 0; i < this.typePalette.length; i++) {
            this.typePalette[i] = this.reader.readString();
        }

        for (let i = 0; i < this.chunkInfos.length; i++) {
            const cx = this.reader.readInt16();
            const cy = this.reader.readInt16();
            const cz = this.reader.readInt16();
            const offset = this.reader.readUInt32();
            const length = this.reader.readUInt32();
            this.chunkInfos[i] = { cx, cy, cz, offset, length };
        }

        this.headerParsed = true;
    }

    /**
     * Processes up to CHUNKS_PER_CYCLE chunks incrementally.
     * Returns true when all chunks have been processed.
     */
    public processNextChunks(): boolean {
        if (!this.headerParsed || this.isComplete) {
            return this.isComplete;
        }

        const endIndex = Math.min(this.currentChunkIndex + this.CHUNKS_PER_CYCLE, this.chunkInfos.length);

        for (let i = this.currentChunkIndex; i < endIndex; i++) {
            this.processChunk(this.chunkInfos[i]);
        }

        this.currentChunkIndex = endIndex;

        if (this.currentChunkIndex >= this.chunkInfos.length) {
            this.isComplete = true;
            console.log("Parsing complete!");
            return true;
        }

        const progress = Math.round((this.currentChunkIndex / this.chunkInfos.length) * 100);
        console.log(`Chunk parsing progress: ${progress}%`);
        return false;
    }

    private processChunk(info: ChunkInfo): void {
        const bytesLeftover = info.offset - this.reader.totalOffset;
        if (bytesLeftover != 0) {
            throw new Error(
                `Fatal: Chunk offset error for [${info.cx},${info.cy},${info.cz}] (${info.offset}) does not match current read position (${this.reader.totalOffset}).`
            );
        }

        const origin = new Vector(info.cx * this.chunkSize, info.cy * this.chunkSize, info.cz * this.chunkSize);

        const objCount = this.reader.readUInt16();
        const chunkKey: string = `${info.cx},${info.cy},${info.cz}`;
        let currentChunkBounds: ObjectSpan = {
            start: this.currentObjectCount,
            end: this.currentObjectCount,
        };

        for (let j = 0; j < objCount; j++) {
            const pX = origin.x + (this.reader.readUInt16() / this.MaxUint16) * this.chunkSize;
            const pY = origin.y + (this.reader.readUInt16() / this.MaxUint16) * this.chunkSize;
            const pZ = origin.z + (this.reader.readUInt16() / this.MaxUint16) * this.chunkSize;
            const pos = new Vector(pX, pY, pZ);

            const scaleIdx = this.reader.readByte();
            const rotIdx = this.reader.readByte();
            const typeIdx = this.reader.readUInt16();

            const customScale = scaleIdx >= 255;
            const customRotation = rotIdx >= 255;

            let scale: Vector;
            if (customScale) {
                const sx = (this.reader.readUInt16() / this.MaxUint16) * this.ScaleMax;
                const sy = (this.reader.readUInt16() / this.MaxUint16) * this.ScaleMax;
                const sz = (this.reader.readUInt16() / this.MaxUint16) * this.ScaleMax;
                scale = new Vector(sx, sy, sz);
            } else {
                scale = this.scalePalette[scaleIdx];
            }

            let rot: Vector;
            if (customRotation) {
                const rx = (this.reader.readUInt16() / this.MaxUint16) * this.RotationRange - this.RotationOffset;
                const ry = (this.reader.readUInt16() / this.MaxUint16) * this.RotationRange - this.RotationOffset;
                const rz = (this.reader.readUInt16() / this.MaxUint16) * this.RotationRange - this.RotationOffset;
                rot = new Vector(rx, ry, rz);
            } else {
                rot = this.rotationPalette[rotIdx];
            }

            try {
                const typeName = this.typePalette[typeIdx];
                const typeId =
                    this.mapSpecificEnum && this.mapSpecificEnum[typeName] !== undefined
                        ? this.mapSpecificEnum[typeName]
                        : mod.RuntimeSpawn_Common[typeName as any];

                if (typeId === undefined) {
                    console.warn(
                        `Unknown object type '${typeName}' encountered in chunk [${info.cx},${info.cy},${info.cz}]. Skipping object.`
                    );
                    continue;
                }
                const uid = this.currentObjectCount++;

                // Pre-compute mod.Vector instances during parsing to avoid repeated conversions during updates
                this.chunkObjects[uid] = {
                    uid: uid, // use the current objectCount to get a unique object id
                    typeId,
                    position: pos,
                    modPosition: pos.toModVector(),
                    modScale: scale.toModVector(),
                    modRotation: rot.toModVector(),
                };
                this.uidToChunk.set(uid, chunkKey);
                currentChunkBounds.end = this.currentObjectCount;
            } catch (e) {
                console.log("Failed to create object, skipping!");
            }
        }
        this.chunkMap.set(chunkKey, currentChunkBounds);
    }

    /**
     * Returns the accumulated chunk map and chunk size when parsing is complete.
     */
    public getResults(): MapObjectData | null {
        if (!this.isComplete) return null;
        return new MapObjectData(this.chunkObjects, this.chunkMap, this.chunkSize, this.uidToChunk);
    }

    public isParsingComplete(): boolean {
        return this.isComplete;
    }
}

class DynamicObjectManager {
    private readonly mapData: MapObjectData;

    private spawnedObjects = new Map<number, mod.SpatialObject>();
    private spawnedTypes = new Map<number, number>();
    private trackedPoints = new Map<string, { point: Vector; radius: number; team: number }>();
    private desiredObjectSet = new Set<number>();
    private objectOwnership: Map<number, number> = new Map();
    private uidToChunk: Map<number, string>;

    private teamMaterialMap: Array<number>;

    constructor(mapData: MapObjectData) {
        this.mapData = mapData;
        this.uidToChunk = mapData.uidToChunk;
        this.teamMaterialMap = [];
        this.teamMaterialMap.push(mod.RuntimeSpawn_Common.BarrierStoneBlock_01_A);
        this.teamMaterialMap.push(mod.RuntimeSpawn_Abbasid.BarrierHesco_01_128x120);
    }

    private getChunkKey(x: number, y: number, z: number): string {
        return `${x},${y},${z}`;
    }

    public addOrUpdateTrackedPoint(key: string, position: mod.Vector, radius: number, team: number): void {
        // TODO - We could optimize memory further by reusing Vector instances in some kind of object pool
        this.trackedPoints.set(key, { point: new Vector().fromModVector(position), radius, team });
    }

    public removeTrackedPoint(key: string): void {
        this.trackedPoints.delete(key);
    }

    public getObjectOwnership(): Map<number, number> {
        return this.objectOwnership;
    }

    // Helper function to get team scores for the UI
    public getTeamScores(): Map<number, number> {
        const scores = new Map<number, number>([
            [1, 0],
            [2, 0],
        ]);
        for (const team of this.objectOwnership.values()) {
            const teamId = team + 1; // Convert team index (0, 1) to team ID (1, 2)
            scores.set(teamId, (scores.get(teamId) ?? 0) + 1);
        }
        return scores;
    }

    public calculateCurrentWinner(): number {
        let team0Count = 0;
        let team1Count = 0;
        for (const team of this.objectOwnership.values()) {
            if (team === 0) team0Count++;
            else if (team === 1) team1Count++;
        }

        if (team0Count > team1Count) {
            return 1; // Team 1 wins
        } else if (team1Count > team0Count) {
            return 2; // Team 2 wins
        } else {
            return 0; // Tie
        }
    }

    public update(): void {
        // Claim objects for teams
        for (const trackedPoint of this.trackedPoints.values()) {
            const pointX = trackedPoint.point.x;
            const pointY = trackedPoint.point.y;
            const pointZ = trackedPoint.point.z;

            const chunkX = Math.floor(pointX / this.mapData.chunkSize);
            const chunkY = Math.floor(pointY / this.mapData.chunkSize);
            const chunkZ = Math.floor(pointZ / this.mapData.chunkSize);

            const chunkRadius = Math.ceil(trackedPoint.radius / this.mapData.chunkSize);

            for (let x = chunkX - chunkRadius; x <= chunkX + chunkRadius; x++) {
                for (let y = chunkY - chunkRadius; y <= chunkY + chunkRadius; y++) {
                    for (let z = chunkZ - chunkRadius; z <= chunkZ + chunkRadius; z++) {
                        const chunkKey = this.getChunkKey(x, y, z);
                        const chunkRange = this.mapData.chunkSegments.get(chunkKey);
                        if (chunkRange) {
                            for (let i = chunkRange.start; i < chunkRange.end; ++i) {
                                const obj = this.mapData.chunkObjects[i];
                                const dx = obj.position.x - pointX;
                                const dy = obj.position.y - pointY;
                                const dz = obj.position.z - pointZ;
                                if (dx * dx + dy * dy + dz * dz <= trackedPoint.radius * trackedPoint.radius) {
                                    this.objectOwnership.set(obj.uid, trackedPoint.team);
                                }
                            }
                        }
                    }
                }
            }
        }

        // Collect desired objects: all owned objects
        this.desiredObjectSet.clear();
        for (const [uid, team] of this.objectOwnership) {
            this.desiredObjectSet.add(uid);
        }

        // Spawn or update objects in desired (don't despawn any, only respawn when ownership changes)
        for (const uid of this.desiredObjectSet) {
            const objToSpawn = this.mapData.chunkObjects[uid];
            const team = this.objectOwnership.get(uid)!;
            const desiredTypeId = this.teamMaterialMap[team] || objToSpawn.typeId;

            if (this.spawnedObjects.has(uid)) {
                const handle = this.spawnedObjects.get(uid)!;
                if (this.spawnedTypes.get(uid) !== desiredTypeId) {
                    mod.UnspawnObject(handle);
                    this.spawnedObjects.delete(uid);
                    this.spawnedTypes.delete(uid);
                    // Spawn new using pre-computed mod.Vector instances
                    const newHandle = mod.SpawnObject(
                        desiredTypeId,
                        objToSpawn.modPosition,
                        objToSpawn.modRotation,
                        objToSpawn.modScale
                    );
                    this.spawnedObjects.set(uid, newHandle);
                    this.spawnedTypes.set(uid, desiredTypeId);
                }
            } else {
                // Spawn new using pre-computed mod.Vector instances
                const handle = mod.SpawnObject(
                    desiredTypeId,
                    objToSpawn.modPosition,
                    objToSpawn.modRotation,
                    objToSpawn.modScale
                );
                this.spawnedObjects.set(uid, handle);
                this.spawnedTypes.set(uid, desiredTypeId);
            }
        }
    }
}

let parser: IncrementalDataParser | null = null;
let objectManager: DynamicObjectManager | null = null;

const teams: mod.HQ[] = [];
const teamsPosition: mod.Vector[] = [];
const teamSpawners: mod.VehicleSpawner[] = [];
let teamDataSet = false;
let vehicleSpawnersEnabled = false;

function updateTeamData() {
    if (teamDataSet) return;

    // only enable spawners and HQ's once we have loaded all of the runtime spatial data
    const team1Hq = mod.GetHQ(1);
    const team2Hq = mod.GetHQ(2);
    teams.push(team1Hq);
    teams.push(team2Hq);

    const team1HqPosition = mod.GetObjectPosition(team1Hq);
    const team2HqPosition = mod.GetObjectPosition(team2Hq);
    teamsPosition.push(team1HqPosition);
    teamsPosition.push(team2HqPosition);

    console.log(
        `Team 1 HQ Position: (${mod.XComponentOf(team1HqPosition)}, ${mod.YComponentOf(team1HqPosition)}, ${mod.ZComponentOf(
            team1HqPosition
        )})`
    );
    console.log(
        `Team 2 HQ Position: (${mod.XComponentOf(team2HqPosition)}, ${mod.YComponentOf(team2HqPosition)}, ${mod.ZComponentOf(
            team2HqPosition
        )})`
    );

    const team1Spawner = mod.GetVehicleSpawner(1);
    const team2Spawner = mod.GetVehicleSpawner(2);
    teamSpawners.push(team1Spawner);
    teamSpawners.push(team2Spawner);
    teamDataSet = true;

    // Vehicle spawners will be enabled when first player spawns on map
}

function enableVehicleSpawners(): void {
    if (vehicleSpawnersEnabled || teamSpawners.length < 2) return;

    mod.SetVehicleSpawnerAutoSpawn(teamSpawners[0], true);
    mod.SetVehicleSpawnerAutoSpawn(teamSpawners[1], true);
    vehicleSpawnersEnabled = true;
    console.log("Vehicle spawners enabled - first player deployed!");
}

// REMOVED: The old UI variables and functions are no longer needed
// let countdownText: mod.UIWidget;
// function getTimeLeft(): { minutes: number; seconds: number; totalSeconds: number }
// let previousTime = 0;
// function updateCountdownUI(): void

/**
 * Main setup function that runs once when the gamemode starts.
 * Initializes the incremental parser.
 */
export function OnGameModeStarted(): void {
    console.log("Initializing incremental spatial data parser...");
    parser = new IncrementalDataParser();
    parser.parseDataPalette();
    console.log("Parser ready. Chunk processing will happen incrementally during updates.");
    mod.SetGameModeTimeLimit(600); // Set time limit to 10 minutes

    // INTEGRATION: Initialize the HUD Manager
    hudManager = new HUDManager();

    // REMOVED: The old manual UI creation
    // const timeLeft = getTimeLeft();
    // mod.AddUIText(...)
    // countdownText = mod.FindUIWidgetWithName("countdown");
}

function FindClosestTeam(vehicle: mod.Vehicle): number {
    let prevDistance = Number.MAX_VALUE;
    let closestIndex = -1;
    const vehPos = mod.GetVehicleState(vehicle, mod.VehicleStateVector.VehiclePosition);

    if (!vehPos) {
        console.log(`[FindClosestTeam] ERROR - vehPos is NULL!`);
        return -1;
    }

    if (teamSpawners.length === 0) {
        console.log(`[FindClosestTeam] ERROR - teamSpawners array is empty!`);
        return -1;
    }

    console.log(
        `[FindClosestTeam] Vehicle Position: (${mod.XComponentOf(vehPos)}, ${mod.YComponentOf(vehPos)}, ${mod.ZComponentOf(
            vehPos
        )}), teamSpawners.length: ${teamSpawners.length}`
    );

    for (let i = 0; i < teamSpawners.length; i++) {
        const spawnerPos = teamsPosition[i];

        if (!spawnerPos) {
            console.log(`[FindClosestTeam] ERROR - Spawn ${i} Position is NULL!`);
            continue;
        }

        console.log(
            `[FindClosestTeam] Spawn ${i} Position: (${mod.XComponentOf(spawnerPos)}, ${mod.YComponentOf(
                spawnerPos
            )}, ${mod.ZComponentOf(spawnerPos)})`
        );
        const dist = mod.DistanceBetween(spawnerPos, vehPos);
        console.log(`[FindClosestTeam] Distance to Spawn ${i}: ${dist}`);

        if (dist < prevDistance) {
            prevDistance = dist;
            closestIndex = i;
        }
    }

    console.log(`[FindClosestTeam] Closest team index: ${closestIndex}, distance: ${prevDistance}`);
    return closestIndex;
}

const VEHICLE_CAP_RADIUS = 30;
const PLAYER_CAP_RADIUS = 15;
/**
 * Global update loop. Continues parsing chunks incrementally until complete,
 * then manages object spawning for all subsequent updates.
 */
export function OngoingGlobal(): void {
    if (!parser) return;

    // Chunk parsing has been implemented to run incrementally to avoid hitting loop execution limits.
    // Async use has been completely avoided to work around the async Promise leaks in the current runtime.
    if (!parser.isParsingComplete()) {
        parser.processNextChunks();

        if (parser.isParsingComplete()) {
            const results = parser.getResults();
            if (results) {
                objectManager = new DynamicObjectManager(results);
                console.log("Spatial manager ready.");
            }
        }
        return;
    }

    if (objectManager) {
        // REMOVED: updateCountdownUI();
        updateTeamData();
        let foundPlayers = 0;
        for (let playerId = 0; playerId < players.length; ++playerId) {
            if (foundPlayers >= playerCount) break;
            const player = players[playerId];

            if (!player) continue;
            foundPlayers++;

            if (!playerDeployments[playerId]) continue;

            const playerPos = mod.GetSoldierState(player, mod.SoldierStateVector.GetPosition);
            const key = playerKeyMap.get(playerId);
            const teamId = playerTeam[playerId];
            if (key && teamId !== undefined) {
                objectManager.addOrUpdateTrackedPoint(key, playerPos, PLAYER_CAP_RADIUS, teamId - 1);
            }
        }

        for (let [vehId, vehicle] of vehicles) {
            if (!vehicle) continue;
            const vehPos = mod.GetVehicleState(vehicle, mod.VehicleStateVector.VehiclePosition);

            const key = vehKeyMap.get(vehId);
            const vehicleTeam = vehicleTeamId.get(vehId);

            // Detailed logging for vehicle capture debugging
            if (!key || vehicleTeam === undefined) {
                console.log(`[Vehicle ${vehId}] SKIPPED - key: ${key ?? "NULL"}, vehicleTeam: ${vehicleTeam ?? "UNDEFINED"}`);
            } else if (!vehPos) {
                console.log(`[Vehicle ${vehId}] SKIPPED - vehPos is NULL. key: ${key}, vehicleTeam: ${vehicleTeam}`);
            } else {
                // const posX = mod.XComponentOf(vehPos);
                // const posY = mod.YComponentOf(vehPos);
                // const posZ = mod.ZComponentOf(vehPos);
                // console.log(
                //     `[Vehicle ${vehId}] CAPTURING - key: ${key}, team: ${vehicleTeam}, pos: (${posX}, ${posY}, ${posZ}), radius: ${VEHICLE_CAP_RADIUS}`
                // );
                objectManager.addOrUpdateTrackedPoint(key, vehPos, VEHICLE_CAP_RADIUS, vehicleTeam);
            }
        }

        if (mod.GetMatchTimeRemaining() <= 0.5) {
            // Calculate final scores and winner
            const winner = objectManager.calculateCurrentWinner();
            const ownership = objectManager!.getObjectOwnership();
            let team0Count = 0;
            let team1Count = 0;
            for (const team of ownership.values()) {
                if (team === 0) team0Count++;
                else if (team === 1) team1Count++;
            }

            console.log(`Final Scores - Team 1: ${team0Count}, Team 2: ${team1Count}. Winner: Team ${winner}`);

            mod.EndGameMode(mod.GetTeam(winner));
        }

        objectManager.update();

        // Update global teamScores cache for UI widgets to use (reuse Map to avoid allocations)
        const scores = objectManager.getTeamScores();
        teamScores.clear();
        for (const [teamId, score] of scores) {
            teamScores.set(teamId, score);
        }

        // INTEGRATION: Refresh all player HUDs with the latest scores
        if (hudManager) {
            hudManager.refreshAll(teamScores);
        }
    }
}

// Cache player and vehicle keys for tracked points to avoid additional string allocations each update
const playerKeyMap: Map<number, string> = new Map<number, string>();
const vehKeyMap: Map<number, string> = new Map<number, string>();

// Manually track players and vehicles to avoid having to either call AllPlayers/OngoingPlayer
// which would add significant overhead per update loop and risk memory leaks
const players = Array<mod.Player | undefined>(256);
const playerDeployments = Array<boolean>(256);
let playerCount = 0;

const playerTeam = new Array<number | undefined>();

const vehicles = new Map<number, mod.Vehicle>();
const vehicleTeamId = new Map<number, number>();

export function OnPlayerDeployed(player: mod.Player): void {
    const playerId = mod.GetObjId(player);
    playerDeployments[playerId] = true;

    // Enable vehicle spawners on first player deployment
    enableVehicleSpawners();
}

export function OnPlayerUndeploy(player: mod.Player): void {
    const playerId = mod.GetObjId(player);
    playerDeployments[playerId] = false;
}

// This will trigger when a Vehicle is destroyed.
export function OnVehicleDestroyed(vehicle: mod.Vehicle): void {
    const vehId = mod.GetObjId(vehicle);
    if (!vehKeyMap.get(vehId)) vehKeyMap.set(vehId, `vehicle_${vehId}`);
    vehicles.delete(vehId);
    objectManager?.removeTrackedPoint(vehKeyMap.get(vehId)!);
    console.log(`Vehicle ${vehId} Destroyed! ${vehKeyMap.get(vehId)}`);
}

// This will trigger when a Vehicle is called into the map.
export function OnVehicleSpawned(vehicle: mod.Vehicle): void {
    const vehId = mod.GetObjId(vehicle);
    if (!vehKeyMap.get(vehId)) vehKeyMap.set(vehId, `vehicle_${vehId}`);
    vehicles.set(vehId, vehicle);

    const closestTeam = FindClosestTeam(vehicle);
    vehicleTeamId.set(vehId, closestTeam);

    const vehPos = mod.GetVehicleState(vehicle, mod.VehicleStateVector.VehiclePosition);
    const posX = vehPos ? mod.XComponentOf(vehPos) : "NULL";
    const posY = vehPos ? mod.YComponentOf(vehPos) : "NULL";
    const posZ = vehPos ? mod.ZComponentOf(vehPos) : "NULL";

    console.log(
        `[OnVehicleSpawned] Vehicle ${vehId} - key: ${vehKeyMap.get(
            vehId
        )}, team: ${closestTeam}, pos: (${posX}, ${posY}, ${posZ})`
    );
}

export function OnPlayerJoinGame(player: mod.Player): void {
    const playerId = mod.GetObjId(player);
    const playerKey: string = `player_${playerId}`;
    playerKeyMap.set(playerId, playerKey);
    players[playerId] = player;
    playerDeployments[playerId] = false;
    playerCount++;
    const team = mod.GetTeam(player);
    const teamId = mod.GetObjId(team);

    playerTeam[playerId] = teamId;

    console.log(`Player ${playerId} Joined!`);
}

export function OnPlayerLeaveGame(playerId: number): void {
    if (objectManager) {
        const key = playerKeyMap.get(playerId);
        if (key) {
            objectManager.removeTrackedPoint(key);
        }
    }

    playerKeyMap.delete(playerId);
    players[playerId] = undefined;
    playerDeployments[playerId] = false;
    playerTeam[playerId] = undefined;
    playerCount--;
    console.log(`Player ${playerId} Left!`);
}

// ==============================================================================================
// UI Classes - BASED ON https://github.com/Mystfit/BF6-CTF-Portal
// ==============================================================================================

// --- UI HELPER: TEAM COLORS ---
// Pre-cache default vectors to avoid creating new ones on every fallback
const DefaultGrayColor = mod.CreateVector(0.5, 0.5, 0.5);
const DefaultWhiteColor = mod.CreateVector(1, 1, 1);

const TeamColors = new Map<number, mod.Vector>([
    [1, mod.CreateVector(0.1, 0.4, 0.8)], // Blue for Team 1
    [2, mod.CreateVector(0.8, 0.3, 0.1)], // Orange for Team 2
]);
const TeamColorsLight = new Map<number, mod.Vector>([
    [1, mod.CreateVector(0.7, 0.8, 1.0)], // Light Blue
    [2, mod.CreateVector(1.0, 0.8, 0.7)], // Light Orange
]);

function GetTeamColorById(teamId: number): mod.Vector {
    return TeamColors.get(teamId) ?? DefaultGrayColor;
}

function GetTeamColorLightById(teamId: number): mod.Vector {
    return TeamColorsLight.get(teamId) ?? DefaultWhiteColor;
}

// Get light team color from team object
function GetTeamColorLight(team: mod.Team): mod.Vector {
    const teamId = mod.GetObjId(team);
    return GetTeamColorLightById(teamId);
}

// Note: VectorClampToRange removed - light colors are already in valid [0,1] range
function VectorClampToRange(vec: mod.Vector, min: number, max: number): mod.Vector {
    const x = Math.max(min, Math.min(max, mod.XComponentOf(vec)));
    const y = Math.max(min, Math.min(max, mod.YComponentOf(vec)));
    const z = Math.max(min, Math.min(max, mod.ZComponentOf(vec)));
    return mod.CreateVector(x, y, z);
}

// Get array of team IDs that are currently leading (highest score)
function GetLeadingTeamIDs(): number[] {
    if (!objectManager) return [];
    const scores = objectManager.getTeamScores();
    let maxScore = -1;
    const leadingTeams: number[] = [];

    for (const [teamId, score] of scores) {
        if (score > maxScore) {
            maxScore = score;
            leadingTeams.length = 0;
            leadingTeams.push(teamId);
        } else if (score === maxScore) {
            leadingTeams.push(teamId);
        }
    }

    return leadingTeams;
}

// Cache of current team scores for UI updates - reused each frame to avoid allocations
let teamScores: Map<number, number> = new Map([
    [1, 0],
    [2, 0],
]);

// --- BASE WIDGET ---
interface TickerWidgetParams {
    position: number[];
    size: number[];
    parent: mod.UIWidget;
    textSize?: number;
    bracketTopBottomLength?: number;
    bracketThickness?: number;
    bgColor?: mod.Vector;
    textColor?: mod.Vector;
    bgAlpha?: number;
    showProgressBar?: boolean;
    progressValue?: number;
    progressDirection?: "left" | "right";
}

abstract class TickerWidget {
    readonly parent: mod.UIWidget;
    readonly position: number[];
    readonly size: number[];
    readonly textSize: number;
    readonly bracketTopBottomLength: number;
    readonly bracketThickness: number;
    protected bgColor: mod.Vector;
    protected textColor: mod.Vector;
    protected bgAlpha: number;

    // Main widgets
    protected columnWidget!: mod.UIWidget;
    protected columnWidgetOutline!: mod.UIWidget;
    protected textWidget!: mod.UIWidget;

    // Progress bar
    protected progressBarContainer: mod.UIWidget | undefined;
    protected progressValue: number;
    protected progressDirection: "left" | "right";
    protected showProgressBar: boolean;

    // Leading indicator brackets (left side)
    protected leftBracketSide: mod.UIWidget | undefined;
    protected leftBracketTop: mod.UIWidget | undefined;
    protected leftBracketBottom: mod.UIWidget | undefined;

    // Leading indicator brackets (right side)
    protected rightBracketSide: mod.UIWidget | undefined;
    protected rightBracketTop: mod.UIWidget | undefined;
    protected rightBracketBottom: mod.UIWidget | undefined;

    // Animation
    isPulsing = false;

    constructor(params: TickerWidgetParams) {
        this.parent = params.parent;
        this.position = params.position ?? [0, 0];
        this.size = params.size ?? [0, 0];
        this.textSize = params.textSize ?? 30;
        this.bracketTopBottomLength = params.bracketTopBottomLength ?? 8;
        this.bracketThickness = params.bracketThickness ?? 2;
        this.bgColor = params.bgColor ?? mod.CreateVector(0.5, 0.5, 0.5);
        this.textColor = params.textColor ?? mod.CreateVector(1, 1, 1);
        this.bgAlpha = params.bgAlpha ?? 0.75;
        this.showProgressBar = params.showProgressBar ?? false;
        this.progressValue = params.progressValue ?? 1.0;
        this.progressDirection = params.progressDirection ?? "left";

        this.createWidgets();
    }

    /**
     * Create all UI widgets for the ticker
     */
    protected createWidgets(): void {
        // Create column container with background color
        this.columnWidget = modlib.ParseUI({
            type: "Container",
            parent: this.parent,
            position: this.position,
            size: [this.size[0], this.size[1]],
            anchor: mod.UIAnchor.TopCenter,
            bgFill: mod.UIBgFill.Blur,
            bgColor: this.bgColor,
            bgAlpha: this.bgAlpha,
        })!;

        // Create column container with outline
        this.columnWidgetOutline = modlib.ParseUI({
            type: "Container",
            parent: this.parent,
            position: this.position,
            size: [this.size[0], this.size[1]],
            anchor: mod.UIAnchor.TopCenter,
            bgFill: mod.UIBgFill.OutlineThin,
            bgColor: this.textColor,
            bgAlpha: 0,
        })!;

        // Create text widget
        this.createTextWidget();

        // Create progress bar if enabled
        if (this.showProgressBar) {
            this.createProgressBar();
        }

        // Create leading indicator brackets
        this.createBrackets();
    }

    /**
     * Create the text widget - can be overridden by subclasses for custom styling
     */
    protected createTextWidget(): void {
        this.textWidget = modlib.ParseUI({
            type: "Text",
            parent: this.columnWidget,
            position: [0, 0],
            size: [this.size[0], 25],
            anchor: mod.UIAnchor.Center,
            textAnchor: mod.UIAnchor.Center,
            textSize: this.textSize,
            textLabel: "",
            textColor: this.textColor,
            bgAlpha: 0,
        })!;
    }

    /**
     * Create progress bar container
     */
    protected createProgressBar(): void {
        const progressWidth = this.size[0] * this.progressValue;
        const anchor = this.progressDirection === "left" ? mod.UIAnchor.CenterLeft : mod.UIAnchor.CenterRight;

        this.progressBarContainer = modlib.ParseUI({
            type: "Container",
            parent: this.columnWidget,
            position: [0, 0],
            size: [progressWidth, this.size[1]],
            anchor: anchor,
            bgFill: mod.UIBgFill.Solid,
            bgColor: this.textColor,
            bgAlpha: 0.9,
        })!;
    }

    /**
     * Set the progress bar value (0.0 to 1.0)
     */
    public setProgressValue(value: number): void {
        this.progressValue = Math.max(0, Math.min(1, value));

        if (this.progressBarContainer) {
            const progressWidth = this.size[0] * this.progressValue;
            mod.SetUIWidgetSize(this.progressBarContainer, mod.CreateVector(progressWidth, this.size[1], 0));
        }
    }

    /**
     * Set the progress bar fill direction
     */
    public setProgressDirection(direction: "left" | "right"): void {
        this.progressDirection = direction;

        if (this.progressBarContainer) {
            const anchor = direction === "left" ? mod.UIAnchor.CenterLeft : mod.UIAnchor.CenterRight;
            mod.SetUIWidgetAnchor(this.progressBarContainer, anchor);
        }
    }

    /**
     * Get the progress bar value
     */
    public getProgressValue(): number {
        return this.progressValue;
    }

    /**
     * Create bracket indicators for highlighting
     * Brackets form open/close square bracket shapes on each side
     */
    protected createBrackets(): void {
        // LEFT BRACKETS (opening bracket [)
        // Left side vertical bar
        this.leftBracketSide = modlib.ParseUI({
            type: "Container",
            parent: this.columnWidget,
            position: [0, 0],
            size: [this.bracketThickness, this.size[1]],
            anchor: mod.UIAnchor.CenterLeft,
            bgFill: mod.UIBgFill.Solid,
            bgColor: this.textColor,
            bgAlpha: 1,
        })!;

        // Left top horizontal bar
        this.leftBracketTop = modlib.ParseUI({
            type: "Container",
            parent: this.columnWidget,
            position: [0, 0],
            size: [this.bracketTopBottomLength, this.bracketThickness],
            anchor: mod.UIAnchor.TopLeft,
            bgFill: mod.UIBgFill.Solid,
            bgColor: this.textColor,
            bgAlpha: 1,
        })!;

        // Left bottom horizontal bar
        this.leftBracketBottom = modlib.ParseUI({
            type: "Container",
            parent: this.columnWidget,
            position: [0, 0],
            size: [this.bracketTopBottomLength, this.bracketThickness],
            anchor: mod.UIAnchor.BottomLeft,
            bgFill: mod.UIBgFill.Solid,
            bgColor: this.textColor,
            bgAlpha: 1,
        })!;

        // RIGHT BRACKETS (closing bracket ])
        // Right side vertical bar
        this.rightBracketSide = modlib.ParseUI({
            type: "Container",
            parent: this.columnWidget,
            position: [0, 0],
            size: [this.bracketThickness, this.size[1]],
            anchor: mod.UIAnchor.CenterRight,
            bgFill: mod.UIBgFill.Solid,
            bgColor: this.textColor,
            bgAlpha: 1,
        })!;

        // Right top horizontal bar
        this.rightBracketTop = modlib.ParseUI({
            type: "Container",
            parent: this.columnWidget,
            position: [0, 0],
            size: [this.bracketTopBottomLength, this.bracketThickness],
            anchor: mod.UIAnchor.TopRight,
            bgFill: mod.UIBgFill.Solid,
            bgColor: this.textColor,
            bgAlpha: 1,
        })!;

        // Right bottom horizontal bar
        this.rightBracketBottom = modlib.ParseUI({
            type: "Container",
            parent: this.columnWidget,
            position: [0, 0],
            size: [this.bracketTopBottomLength, this.bracketThickness],
            anchor: mod.UIAnchor.BottomRight,
            bgFill: mod.UIBgFill.Solid,
            bgColor: this.textColor,
            bgAlpha: 1,
        })!;

        // Hide brackets by default
        this.showBrackets(false);
    }

    /**
     * Update the text displayed in the widget
     */
    protected updateText(message: mod.Message): void {
        mod.SetUITextLabel(this.textWidget, message);
    }

    /**
     * Show or hide the bracket indicators
     */
    protected showBrackets(show: boolean): void {
        if (this.leftBracketTop) mod.SetUIWidgetVisible(this.leftBracketTop, show);
        if (this.leftBracketSide) mod.SetUIWidgetVisible(this.leftBracketSide, show);
        if (this.leftBracketBottom) mod.SetUIWidgetVisible(this.leftBracketBottom, show);
        if (this.rightBracketSide) mod.SetUIWidgetVisible(this.rightBracketSide, show);
        if (this.rightBracketTop) mod.SetUIWidgetVisible(this.rightBracketTop, show);
        if (this.rightBracketBottom) mod.SetUIWidgetVisible(this.rightBracketBottom, show);
    }

    /**
     * Refresh the widget - should be implemented by subclasses
     */
    abstract refresh(): void;
}

// --- SCORE TICKER ---
interface ScoreTickerParams extends TickerWidgetParams {
    teamId: number;
}

class ScoreTicker extends TickerWidget {
    readonly teamId: number;

    private currentScore: number = -1;
    private isLeading: boolean = false;

    constructor(params: ScoreTickerParams) {
        // Get team colors before calling super
        const teamId = params.teamId;
        const teamColor = GetTeamColorById(teamId);
        const textColor = GetTeamColorLightById(teamId); // Light colors already in [0,1] range

        // Call parent constructor with team-specific colors
        super({
            position: params.position,
            size: params.size,
            parent: params.parent,
            textSize: params.textSize,
            bracketTopBottomLength: params.bracketTopBottomLength,
            bracketThickness: params.bracketThickness,
            bgColor: teamColor,
            textColor: textColor,
            bgAlpha: 0.75,
        });

        this.teamId = teamId;

        this.refresh();
    }

    /**
     * Update the score display and leading indicator
     */
    public updateScore(): void {
        const score = teamScores.get(this.teamId) ?? 0;

        // Only update if score has changed
        if (this.currentScore !== score) {
            this.currentScore = score;
            this.updateText(mod.Message(score));

            // Show brackets only if this team is the sole leader (no ties)
            let leadingTeams = GetLeadingTeamIDs();
            // console.log(`Leading teams: ${leadingTeams.join(", ")}`);
            if (leadingTeams.length === 1 && leadingTeams.includes(this.teamId)) {
                this.setLeading(true);
            } else {
                this.setLeading(true);
            }
        }
    }

    /**
     * Set whether this team is currently in the lead
     * @param isLeading True if this team is leading (not tied)
     */
    public setLeading(isLeading: boolean): void {
        // console.log(`Score ticker leading: ${isLeading}`);

        this.isLeading = isLeading;
        this.showBrackets(isLeading);
    }

    /**
     * Get the current score
     */
    public getScore(): number {
        return this.currentScore;
    }

    /**
     * Get the team ID
     */
    public getTeamId(): number {
        return this.teamId;
    }

    /**
     * Refresh both score and leading status
     */
    public refresh(): void {
        this.updateScore();
    }

    /**
     * Clean up UI widgets
     */
    public destroy(): void {
        mod.DeleteUIWidget(this.columnWidget);
        mod.DeleteUIWidget(this.columnWidgetOutline);
    }
}

interface RoundTimerParams {
    position: number[];
    size: number[];
    parent: mod.UIWidget;
    textSize?: number;
    seperatorPadding?: number;
    bracketTopBottomLength?: number;
    bracketThickness?: number;
    bgColor?: mod.Vector;
    textColor?: mod.Vector;
    bgAlpha?: number;
}

class RoundTimer extends TickerWidget {
    private currentTimeSeconds: number = -1;
    private currentTimeMinutes: number = -1;
    private seperatorPadding: number;
    private secondsText: mod.UIWidget;
    private minutesText: mod.UIWidget;
    private seperatorText: mod.UIWidget;

    constructor(params: RoundTimerParams) {
        // Call parent constructor with default neutral colors if not specified
        super({
            position: params.position,
            size: params.size,
            parent: params.parent,
            textSize: params.textSize,
            bracketTopBottomLength: params.bracketTopBottomLength,
            bracketThickness: params.bracketThickness,
            bgColor: params.bgColor ?? mod.CreateVector(0.2, 0.2, 0.2),
            textColor: params.textColor ?? mod.CreateVector(1, 1, 1),
            bgAlpha: params.bgAlpha ?? 0.75,
        });

        this.seperatorPadding = params.seperatorPadding ?? 16;

        this.secondsText = modlib.ParseUI({
            type: "Text",
            parent: this.columnWidget,
            position: [this.seperatorPadding, 0],
            size: [30, 24],
            anchor: mod.UIAnchor.Center,
            textAnchor: mod.UIAnchor.CenterLeft,
            textSize: this.textSize,
            textLabel: "",
            textColor: this.textColor,
            bgAlpha: 0,
        })!;

        this.minutesText = modlib.ParseUI({
            type: "Text",
            parent: this.columnWidget,
            position: [-this.seperatorPadding, 0],
            size: [5, 24],
            anchor: mod.UIAnchor.Center,
            textAnchor: mod.UIAnchor.CenterRight,
            textSize: this.textSize,
            textLabel: "",
            textColor: this.textColor,
            bgAlpha: 0,
        })!;

        this.seperatorText = modlib.ParseUI({
            type: "Text",
            parent: this.columnWidget,
            position: [0, 0],
            size: [30, 24],
            anchor: mod.UIAnchor.Center,
            textAnchor: mod.UIAnchor.Center,
            textSize: this.textSize,
            textLabel: mod.stringkeys.score_timer_seperator,
            textColor: this.textColor,
            bgAlpha: 0,
        })!;

        this.refresh();
    }

    /**
     * Update the timer display with remaining game time
     */
    public updateTime(): void {
        const remainingTime = mod.GetMatchTimeRemaining();
        const timeSeconds = Math.floor(remainingTime);

        // Only update if time has changed
        if (this.currentTimeSeconds !== timeSeconds) {
            // Update time values and floor/pad values
            this.currentTimeSeconds = timeSeconds % 60;
            this.currentTimeMinutes = Math.floor(timeSeconds / 60);
            const secondsTensDigit = Math.floor(this.currentTimeSeconds / 10);
            const secondsOnesDigit = this.currentTimeSeconds % 10;

            // Update text labels
            mod.SetUITextLabel(this.minutesText, mod.Message(mod.stringkeys.score_timer_minutes, this.currentTimeMinutes));
            mod.SetUITextLabel(
                this.secondsText,
                mod.Message(mod.stringkeys.score_timer_seconds, secondsTensDigit, secondsOnesDigit)
            );
        }
    }

    /**
     * Refresh - called by the HUD update loop to refresh timer display
     */
    public refresh(): void {
        this.updateTime();
    }

    /**
     * Clean up UI widgets
     */
    public destroy(): void {
        mod.DeleteUIWidget(this.columnWidget);
        mod.DeleteUIWidget(this.columnWidgetOutline);
    }
}

// --- SCORE PROGRESS BAR ---
class ScoreProgressBar {
    private rootContainer: mod.UIWidget;
    private team1Bar: mod.UIWidget;
    private team2Bar: mod.UIWidget;
    private barWidth: number;

    constructor(params: any) {
        this.barWidth = params.size[0];
        this.rootContainer = modlib.ParseUI({
            type: "Container",
            parent: params.parent,
            position: params.position,
            size: [this.barWidth, params.size[1]],
            anchor: mod.UIAnchor.TopCenter,
            bgFill: mod.UIBgFill.Blur,
            bgColor: [0, 0, 0],
            bgAlpha: 0,
        })!;

        mod.SetUIWidgetBgAlpha(this.rootContainer, 0);

        const barHeight = params.size[1];
        const team1Color = GetTeamColorById(1);
        const team2Color = GetTeamColorById(2);

        this.team1Bar = modlib.ParseUI({
            type: "Container",
            parent: this.rootContainer,
            position: [0, 0],
            size: [this.barWidth / 2, barHeight],
            anchor: mod.UIAnchor.CenterLeft,
            bgFill: mod.UIBgFill.Solid,
            bgColor: team1Color,
            bgAlpha: 0.9,
        })!;

        mod.SetUIWidgetBgFill(this.team1Bar, mod.UIBgFill.Solid);
        mod.SetUIWidgetBgColor(this.team1Bar, team1Color);
        mod.SetUIWidgetBgAlpha(this.team1Bar, 0.9);

        this.team2Bar = modlib.ParseUI({
            type: "Container",
            parent: this.rootContainer,
            position: [0, 0],
            size: [this.barWidth / 2, barHeight],
            anchor: mod.UIAnchor.CenterRight,
            bgFill: mod.UIBgFill.Solid,
            bgColor: team2Color,
            bgAlpha: 0.9,
        })!;

        mod.SetUIWidgetBgFill(this.team2Bar, mod.UIBgFill.Solid);
        mod.SetUIWidgetBgColor(this.team2Bar, team2Color);
        mod.SetUIWidgetBgAlpha(this.team2Bar, 0.9);
    }

    public refresh(scores: Map<number, number>): void {
        const score1 = scores.get(1) ?? 0;
        const score2 = scores.get(2) ?? 0;
        const totalScore = score1 + score2;
        const progress = totalScore === 0 ? 0.5 : score1 / totalScore;

        mod.SetUIWidgetSize(
            this.team1Bar,
            mod.CreateVector(this.barWidth * progress, mod.YComponentOf(mod.GetUIWidgetSize(this.team1Bar)), 0)
        );
        mod.SetUIWidgetSize(
            this.team2Bar,
            mod.CreateVector(this.barWidth * (1.0 - progress), mod.YComponentOf(mod.GetUIWidgetSize(this.team2Bar)), 0)
        );
    }
    public destroy(): void {
        mod.DeleteUIWidget(this.rootContainer);
    }
}

// --- GLOBAL SCORE HUD ---
class ConquestScoreHUD {
    private rootWidget: mod.UIWidget;
    private teamScoreTickers: Map<number, ScoreTicker> = new Map();
    private timerTicker: RoundTimer;
    private scoreBar: ScoreProgressBar;

    constructor() {
        // Create global UI widget (not tied to any specific player)
        this.rootWidget = modlib.ParseUI({
            type: "Container",
            size: [700, 100],
            position: [0, 20, 0],
            anchor: mod.UIAnchor.TopCenter,
            bgFill: mod.UIBgFill.Blur,
            bgColor: [0, 0, 0],
            bgAlpha: 0.0,
        })!;

        const teamScoreSpacing = 490;
        const teamScorePaddingTop = 68;
        const teamWidgetSize = [76, 30];

        this.teamScoreTickers.set(
            1,
            new ScoreTicker({
                parent: this.rootWidget,
                position: [-teamScoreSpacing * 0.5, teamScorePaddingTop],
                size: teamWidgetSize,
                teamId: 1,
                textSize: 24,
            })
        );
        this.teamScoreTickers.set(
            2,
            new ScoreTicker({
                parent: this.rootWidget,
                position: [teamScoreSpacing * 0.5, teamScorePaddingTop],
                size: teamWidgetSize,
                teamId: 2,
                textSize: 24,
            })
        );

        const barWidth = teamScoreSpacing - teamWidgetSize[0] - 20;
        const barPosY = teamScorePaddingTop + teamWidgetSize[1] / 2 - 6; // Center vertically
        this.scoreBar = new ScoreProgressBar({ position: [0, barPosY], size: [barWidth, 12], parent: this.rootWidget });

        this.timerTicker = new RoundTimer({ position: [0, 48], parent: this.rootWidget, textSize: 26, size: [100, 22] });
    }

    public refresh(scores: Map<number, number>): void {
        this.teamScoreTickers.get(1)?.refresh();
        this.teamScoreTickers.get(2)?.refresh();
        this.timerTicker.refresh();
        this.scoreBar.refresh(scores);
    }

    public destroy(): void {
        this.teamScoreTickers.forEach((t) => t.destroy());
        this.timerTicker.destroy();
        this.scoreBar.destroy();
        mod.DeleteUIWidget(this.rootWidget);
    }
}

// --- HUD MANAGER ---
let hudManager: HUDManager | null = null;

class HUDManager {
    private globalHud: ConquestScoreHUD;

    constructor() {
        this.globalHud = new ConquestScoreHUD();
    }

    public refreshAll(scores: Map<number, number>): void {
        this.globalHud.refresh(scores);
    }

    public destroy(): void {
        this.globalHud.destroy();
    }
}
