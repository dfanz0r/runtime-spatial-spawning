# RuntimeMigrator Binary Format

This document is the authoritative layout for the binary payload produced by `RuntimeMigrator.SpatialBinaryWriter` and consumed by `runtime-code/runtimeSpawn.ts`.

The payload is later encoded into `.strings.json` with the custom base16 encoding described at the end of this file. The `.strings.json` wrapper is not part of the binary format itself.

## Format version

Current version: `1`.

Readers should reject unsupported versions.

## Encoding conventions

- All numeric primitives are little-endian, matching .NET `BinaryWriter` and the TypeScript runtime `DataView(..., true)` reads.
- `float32` values are IEEE-754 single precision.
- `string` values use .NET `BinaryWriter.Write(string)` encoding:
  - a 7-bit encoded integer byte length prefix;
  - followed by exactly that many UTF-8 bytes.
- Offsets in the chunk index are absolute byte offsets from the start of the decoded binary payload.
- There is no alignment or padding between fields.

## High-level layout

Fields are written sequentially in this exact order:

```text
MapFileV1
  Header
    uint16   version
    float32  chunkSize
    string   mapType
    int32    encodedObjectCount
    uint16   chunkCount
    float32  minBoundsX
    float32  minBoundsY
    float32  minBoundsZ
    float32  maxBoundsX
    float32  maxBoundsY
    float32  maxBoundsZ
    uint16   scalePaletteCount
    uint16   rotationPaletteCount
    uint16   typePaletteCount

  Palettes
    Vector3Float scalePalette[scalePaletteCount]
    Vector3Float rotationPalette[rotationPaletteCount]
    string       typePalette[typePaletteCount]

  Chunk index
    ChunkIndexEntry chunkIndex[chunkCount]

  Chunk data
    ChunkData chunks[chunkCount]
```

`Vector3Float` is three `float32` values in `x, y, z` order.

## Header fields

| Field | Type | Description |
|---|---:|---|
| `version` | `uint16` | Format version. Currently `1`. |
| `chunkSize` | `float32` | World-space chunk size. Currently always `64.0`. |
| `mapType` | `string` | Map type inferred from the first `Static` object type, e.g. `MP_Capstone_Block` -> `Capstone`; otherwise `Unknown`. |
| `encodedObjectCount` | `int32` | Number of objects encoded in chunk data. This is the number of compressible objects, not the total source JSON object count. |
| `chunkCount` | `uint16` | Number of chunk index entries and chunk data blocks. |
| `minBounds` | `float32 x 3` | Minimum world-space position of encoded objects. If zero objects are encoded, this is `(0, 0, 0)`. |
| `maxBounds` | `float32 x 3` | Maximum world-space position of encoded objects. If zero objects are encoded, this is `(0, 0, 0)`. |
| `scalePaletteCount` | `uint16` | Number of scale palette entries. Maximum `255` in the current writer. |
| `rotationPaletteCount` | `uint16` | Number of rotation palette entries. Maximum `255` in the current writer. |
| `typePaletteCount` | `uint16` | Number of type palette entries. Maximum `65535`. |

## Palettes

Palette entries immediately follow the header counts.

### Scale palette

```text
for i in 0..scalePaletteCount-1:
  float32 scaleX
  float32 scaleY
  float32 scaleZ
```

Scale palette values are stored as raw float32 triplets. Scales are rounded to two decimals during classification before palette lookup.

### Rotation palette

```text
for i in 0..rotationPaletteCount-1:
  float32 rotationX
  float32 rotationY
  float32 rotationZ
```

Rotation palette values are stored as raw float32 triplets in radians.

### Type palette

```text
for i in 0..typePaletteCount-1:
  string typeName
```

Type names are the spatial object type strings used by the runtime to resolve spawnable object types.

## Palette index rules

| Palette | Stored object field | Indexed range | Inline sentinel | Inline payload |
|---|---:|---:|---:|---|
| Scale | `scaleIndex: uint8` | `0..254` | `255` | `uint16 x 3` quantized scale |
| Rotation | `rotationIndex: uint8` | `0..254` | `255` | `uint16 x 3` quantized rotation |
| Type | `typeId: uint16` | `0..65534` | none | none |

The writer stores at most 255 scale palette entries and 255 rotation palette entries, corresponding to byte indices `0..254`. Additional unique scale or rotation values are written inline with sentinel value `255`.

Type palette entries do not have inline storage. The type palette count and each object type id are encoded as `uint16`, so the type palette must fit within `uint16`.

## Chunk index

Each chunk index entry is exactly 14 bytes:

```text
ChunkIndexEntry
  int16   chunkX
  int16   chunkY
  int16   chunkZ
  uint32  fileOffset
  uint32  byteLength
```

| Field | Type | Description |
|---|---:|---|
| `chunkX`, `chunkY`, `chunkZ` | `int16` | Chunk coordinates computed as `floor(position / chunkSize)` for each axis. |
| `fileOffset` | `uint32` | Absolute byte offset to this chunk's `ChunkData` from the start of the binary payload. |
| `byteLength` | `uint32` | Number of bytes occupied by this chunk's `ChunkData`, including the `objectCount` field. |

Chunk index entries are sorted by `(chunkX, chunkY, chunkZ)` for deterministic output.

## Chunk data

A chunk data block starts at the corresponding `fileOffset` from the chunk index.

```text
ChunkData
  uint16 objectCount
  ObjectRecord objects[objectCount]
```

| Field | Type | Description |
|---|---:|---|
| `objectCount` | `uint16` | Number of encoded objects in this chunk. |
| `objects` | variable | Object records in the same order they were encountered in source traversal for this chunk. |

Chunk data does **not** store the chunk coordinate or origin. Readers derive the origin from the chunk index:

```text
origin = (chunkX * chunkSize, chunkY * chunkSize, chunkZ * chunkSize)
```

## Object record

Object records are variable length. The base record is 10 bytes. Inline scale and/or inline rotation each add 6 bytes.

```text
ObjectRecord
  uint16 localPosX
  uint16 localPosY
  uint16 localPosZ
  uint8  scaleIndex
  uint8  rotationIndex
  uint16 typeId

  if scaleIndex == 255:
    uint16 customScaleX
    uint16 customScaleY
    uint16 customScaleZ

  if rotationIndex == 255:
    uint16 customRotationX
    uint16 customRotationY
    uint16 customRotationZ
```

| Offset | Size | Field | Type | Description |
|---:|---:|---|---:|---|
| 0 | 2 | `localPosX` | `uint16` | Quantized local X position relative to chunk origin. |
| 2 | 2 | `localPosY` | `uint16` | Quantized local Y position relative to chunk origin. |
| 4 | 2 | `localPosZ` | `uint16` | Quantized local Z position relative to chunk origin. |
| 6 | 1 | `scaleIndex` | `uint8` | `0..254` palette index, `255` means inline scale follows. |
| 7 | 1 | `rotationIndex` | `uint8` | `0..254` palette index, `255` means inline rotation follows. |
| 8 | 2 | `typeId` | `uint16` | Index into the type palette. |
| 10 | 6 | `customScale` | `uint16 x 3` | Present only when `scaleIndex == 255`. |
| 10 or 16 | 6 | `customRotation` | `uint16 x 3` | Present only when `rotationIndex == 255`. Starts at offset 16 if custom scale is present; otherwise offset 10. |

Object sizes:

| Inline fields | Size |
|---|---:|
| none | 10 bytes |
| scale only | 16 bytes |
| rotation only | 16 bytes |
| scale and rotation | 22 bytes |

## Quantization

All quantized values are clamped to `[0, 65535]` after rounding.

The C# writer uses `Math.Round`, which defaults to midpoint-to-even rounding. Runtime dequantization uses normal floating-point division.

### Position

Positions are stored relative to the chunk origin.

```text
chunkCoord = floor(position / chunkSize)
origin     = chunkCoord * chunkSize
localPos   = position - origin
encoded    = clamp(round(localPos / chunkSize * 65535), 0, 65535)

decodedWorldPosition = origin + (encoded / 65535 * chunkSize)
```

### Inline scale

Scale palette values are stored as raw float32 values. Inline scale values are quantized using `ScaleMax = 100.0`.

```text
encoded = clamp(round(scale / 100.0 * 65535), 0, 65535)
decoded = encoded / 65535 * 100.0
```

Values above `100.0` are clamped when stored inline.

### Inline rotation

Rotation palette values are stored as raw float32 radians. Inline rotation values are quantized over `[-PI, PI]` using `RotationRange = 2 * PI` and `RotationOffset = PI`.

```text
encoded = clamp(round((rotation + PI) / (2 * PI) * 65535), 0, 65535)
decoded = (encoded / 65535 * (2 * PI)) - PI
```

## Object classification and ordering

Only compressible objects are encoded in this binary format. Retained objects are written to the companion `_filtered.spatial.json` file.

Current source traversal order:

1. `Portal_Dynamic` array in source order.
2. `Static` array in source order.

This traversal controls:

- which objects are encoded;
- object order within each chunk;
- scale, rotation, and type palette first-encounter order.

Chunks are then written in sorted `(x, y, z)` order. Repeated conversions of the same input should produce byte-identical binary payloads.

## Writer validation and limits

The writer validates these narrowing limits before writing:

- `chunkCount <= 65535`
- each chunk coordinate fits `int16` (`-32768..32767`)
- per-chunk `objectCount <= 65535`
- `typePaletteCount <= 65535`

Format limits and current constants:

| Item | Limit / value |
|---|---:|
| `version` | `1` |
| `chunkSize` | `64.0` |
| `encodedObjectCount` | `int32` max |
| `chunkCount` | `uint16` max (`65535`) |
| chunk coordinate | `int16` range (`-32768..32767`) |
| per-chunk object count | `uint16` max (`65535`) |
| scale palette entries | `255` (`0..254`, sentinel `255`) |
| rotation palette entries | `255` (`0..254`, sentinel `255`) |
| type palette entries | `uint16` max (`65535`) |
| inline scale max | `100.0` |
| inline rotation range | `[-PI, PI]` |

## `.strings.json` wrapper

After the binary payload is written, `StringsJsonWriter` encodes it and writes a JSON object.

The default tool encoding is custom base16. For Portal website upload, prefer `--safe-base32`; it uses no letters, whitespace, quotes, slashes, backslashes, or `#` to reduce word-filter corruption. The current minimal `runtime-code/runtimeSpawn.ts` script expects safe-base32 by default.

A test-only CLI mode, `--base64`, writes the same binary payload as base64 chunks instead. Base64 is JSON-safe, but it is not recommended for Portal upload because the Portal website word filter can corrupt alphabetic data.

### Default custom base16 encoding

Alphabet:

```text
Index:  0 1 2 3 4 5 6 7 8 9 A B C D E F
Char:   _ - , . : ` ~ ' + = ^ % < } ] <space>
String: "_-,.:`~'+=^%<}] "
```

Each byte is encoded as two characters: high nibble first, then low nibble.

The encoded text is split into chunks of 200 characters, equivalent to 100 binary bytes per full chunk. JSON property names are uppercase hexadecimal sequence keys:

```text
A0, A1, A2, ..., A9, AA, AB, ...
```

The final chunk may be shorter than 200 characters. Runtime readers concatenate chunks in key index order and decode back to the binary payload before parsing the format above.

### Portal-safe custom base32 encoding

When the C# tool is run with `--safe-base32`, the binary payload is encoded with this 32-character alphabet:

```text
Index:  0 1 2 3 4 5 6 7 8 9 A B C D E F G H I J K L M N O P Q R S T U V
Char:   0 1 2 3 4 5 6 7 8 9 ! $ % & ( ) * + , - . : ; = ? @ [ ] ^ _ { }
String: "0123456789!$%&()*+,-.:;=?@[]^_{}"
```

This encoding packs 5 bits per character without padding. A full 200-character chunk decodes to 125 binary bytes. The final chunk may be shorter; decoders use `floor(characterCount * 5 / 8)` output bytes and ignore trailing zero padding bits.

### Test base64 encoding

When the C# tool is run with `--base64`, the binary payload is encoded with standard base64 (`Convert.ToBase64String`) and split into the same 200-character chunk keys. This mode is intended for experimentation/comparison and is not consumed by the current minimal Portal runtime script.
