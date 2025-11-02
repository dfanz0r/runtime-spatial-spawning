// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 3-Clause License.
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
class RuntimeStringsReader {
    private readonly strings: { [key: string]: string };
    private chunkIndex = 0;
    private initialized = false;
    private chunkData: Uint8Array = new Uint8Array(DECODE_CHUNK_SIZE);
    private currentChunkLength: number = 0;
    private chunkOffset = 0;
    public eof = false;

    constructor() {
        this.strings = (mod as any).strings;
        if (typeof this.strings !== "object" || this.strings === null)
            throw new Error("Runtime object 'mod.strings' is not available.");
    }

    private updateDataChunk(): boolean {
        if (this.eof) return false;

        const key = `A${this.chunkIndex.toString(16).toUpperCase()}`;
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
    private stringReader: RuntimeStringsReader;
    private _isStringReaderEof: boolean = false;

    private static readonly BUFFER_SIZE = 100;
    private tempStringBuffer: Uint8Array = new Uint8Array(0);

    constructor(reader: RuntimeStringsReader) {
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
};

class Vector {
    constructor(public x: number = 0, public y: number = 0, public z: number = 0) {}

    private cachedModVec: mod.Vector | undefined;

    public toModVector(): mod.Vector {
        this.cachedModVec ??= mod.CreateVector(this.x, this.y, this.z);

        return this.cachedModVec;
    }

    public fromModVector(vec: mod.Vector): Vector {
        this.x = mod.XComponentOf(vec);
        this.y = mod.YComponentOf(vec);
        this.z = mod.ZComponentOf(vec);
        return this;
    }
}

const ZeroVector = new Vector(0, 0, 0);

interface SpatialObject {
    uid: number;
    typeId: number;
    position: Vector;
    scale: Vector;
    rotation: Vector;
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

    constructor(chunkObjects: Array<SpatialObject>, chunkSegments: Map<string, ObjectSpan>, chunkSize: number) {
        this.chunkObjects = chunkObjects;
        this.chunkSegments = chunkSegments;
        this.chunkSize = chunkSize;
        this.objectCount = chunkObjects.length;
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
        this.reader = new AsyncBinaryReader(new RuntimeStringsReader());

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
                console.log(`${pos.x} ${pos.y} ${pos.z} - ${rot.x} ${rot.y} ${rot.z}`);
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

                this.chunkObjects[uid] = {
                    uid: uid, // use the current objectCount to get a unique object id
                    typeId,
                    position: pos,
                    scale: scale,
                    rotation: rot,
                };
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
        return new MapObjectData(this.chunkObjects, this.chunkMap, this.chunkSize);
    }

    public isParsingComplete(): boolean {
        return this.isComplete;
    }
}

class DynamicObjectManager {
    private readonly mapData: MapObjectData;

    private readonly spawnRadiusSq: number;
    private readonly chunkSearchRadius: number;

    private spawnedObjects = new Map<number, mod.SpatialObject>();
    private trackedPoints = new Map<string, Vector>();
    private desiredObjectSet = new Set<number>();

    constructor(mapData: MapObjectData, spawnRadius: number) {
        this.mapData = mapData;
        this.spawnRadiusSq = spawnRadius * spawnRadius;
        this.chunkSearchRadius = Math.ceil(spawnRadius / mapData.chunkSize);
    }

    private getChunkKey(x: number, y: number, z: number): string {
        return `${x},${y},${z}`;
    }

    public addOrUpdateTrackedPoint(key: string, position: mod.Vector): void {
        // TODO - We could optimize memory further by reusing Vector instances in some kind of object pool
        this.trackedPoints.set(key, new Vector().fromModVector(position));
    }

    public removeTrackedPoint(key: string): void {
        this.trackedPoints.delete(key);
    }

    public update(): void {
        this.desiredObjectSet.clear();

        const chunkObjects = this.mapData.chunkObjects;

        for (const position of this.trackedPoints.values()) {
            const pointX = position.x;
            const pointY = position.y;
            const pointZ = position.z;

            const chunkX = Math.floor(pointX / this.mapData.chunkSize);
            const chunkY = Math.floor(pointY / this.mapData.chunkSize);
            const chunkZ = Math.floor(pointZ / this.mapData.chunkSize);

            for (let x = chunkX - this.chunkSearchRadius; x <= chunkX + this.chunkSearchRadius; x++) {
                for (let y = chunkY - this.chunkSearchRadius; y <= chunkY + this.chunkSearchRadius; y++) {
                    for (let z = chunkZ - this.chunkSearchRadius; z <= chunkZ + this.chunkSearchRadius; z++) {
                        const chunkRange = this.mapData.chunkSegments.get(this.getChunkKey(x, y, z));
                        if (chunkRange) {
                            for (let i = chunkRange.start; i < chunkRange.end; ++i) {
                                const obj = chunkObjects[i];
                                const dx = obj.position.x - pointX;
                                const dy = obj.position.y - pointY;
                                const dz = obj.position.z - pointZ;
                                if (dx * dx + dy * dy + dz * dz <= this.spawnRadiusSq) {
                                    this.desiredObjectSet.add(obj.uid);
                                }
                            }
                        }
                    }
                }
            }
        }

        for (const [uid, handle] of this.spawnedObjects.entries()) {
            if (!this.desiredObjectSet.has(uid)) {
                mod.UnspawnObject(handle);
                this.spawnedObjects.delete(uid);
            }
        }

        for (const uid of this.desiredObjectSet) {
            if (!this.spawnedObjects.has(uid)) {
                const objToSpawn = chunkObjects[uid];
                const pos = objToSpawn.position;
                const handle = mod.SpawnObject(
                    objToSpawn.typeId,
                    pos.toModVector(),
                    objToSpawn.rotation.toModVector(),
                    objToSpawn.scale.toModVector()
                );
                this.spawnedObjects.set(uid, handle);
            }
        }
    }
}

let parser: IncrementalDataParser | null = null;
let objectManager: DynamicObjectManager | null = null;

/**
 * Main setup function that runs once when the gamemode starts.
 * Initializes the incremental parser.
 */
export function OnGameModeStarted(): void {
    console.log("Initializing incremental spatial data parser...");
    parser = new IncrementalDataParser();
    parser.parseDataPalette();
    console.log("Parser ready. Chunk processing will happen incrementally during updates.");
}

// TODO - We should probably manually track vehicles like players to avoid overhead
// and potential memory leaks from the runtime
export function OngoingVehicle(vehicle: mod.Vehicle): void {
    if (!objectManager) return;
    const vehId = mod.GetObjId(vehicle);
    if (!vehKeyMap.get(vehId)) vehKeyMap.set(vehId, `vehicle_${vehId}`);
    const position = mod.GetVehicleState(vehicle, mod.VehicleStateVector.VehiclePosition);
    const key = vehKeyMap.get(vehId);
    if (key) {
        objectManager.addOrUpdateTrackedPoint(key, position);
    }
}

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
                const SPAWN_RADIUS = 40;
                objectManager = new DynamicObjectManager(results, SPAWN_RADIUS);
                console.log(`Spatial manager ready. Tracking objects within a ${SPAWN_RADIUS}m radius.`);
            }
        }
        return;
    }

    if (objectManager) {
        for (let playerId = 0; playerId < players.length; ++playerId) {
            const player = players[playerId];

            if (!player) continue;

            if (!playerDeployments[playerId]) continue;

            const playerPos = mod.GetSoldierState(player, mod.SoldierStateVector.GetPosition);
            const key = playerKeyMap.get(playerId);
            if (key) {
                objectManager.addOrUpdateTrackedPoint(key, playerPos);
            }
        }

        objectManager.update();
    }
}

// Cache player and vehicle keys for tracked points to avoid additional string allocations each update
const playerKeyMap: Map<number, string> = new Map<number, string>();
const vehKeyMap: Map<number, string> = new Map<number, string>();

// Manually track players to avoid having to either call AllPlayers/OngoingPlayer
// which would add significant overhead per update loop and risk memory leaks
const players = Array<mod.Player | undefined>(256);
const playerDeployments = Array<boolean>(256);

export function OnPlayerDeployed(player: mod.Player): void {
    const playerId = mod.GetObjId(player);
    playerDeployments[playerId] = true;
}

export function OnPlayerUndeploy(player: mod.Player): void {
    const playerId = mod.GetObjId(player);
    playerDeployments[playerId] = false;
}

export function OnPlayerJoinGame(player: mod.Player): void {
    const playerId = mod.GetObjId(player);
    const playerKey: string = `player_${playerId}`;
    playerKeyMap.set(playerId, playerKey);
    players[playerId] = player;
    playerDeployments[playerId] = false;

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
    console.log(`Player ${playerId} Left!`);
}
