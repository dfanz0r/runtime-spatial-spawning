# Map Binary Format

This document describes the binary layout of the Map file format used by the project.

---

## Top-level structure

```
MapFile {
  // Header
  version: uint16,                    // 2 bytes
  chunkSize: float32,                 // 4 bytes
  mapType: string,                    // Variable length (null-terminated)
  totalObjects: int32,                // 4 bytes (total number of objects for pre-allocation)
  chunkCount: uint16,                 // 2 bytes

  // World Bounds
  minBounds: float32[3],              // 12 bytes (minX, minY, minZ)
  maxBounds: float32[3],              // 12 bytes (maxX, maxY, maxZ)

  // Palette Counts
  scalePaletteCount: uint16,          // 2 bytes
  rotationPaletteCount: uint16,       // 2 bytes
  typePaletteCount: uint16,           // 2 bytes

  // Scale Palette (computed from data)
  scales: float32[scalePaletteCount][3],     // 12 bytes per entry

  // Rotation Palette (computed from data, radians)
  rotations: float32[rotationPaletteCount][3], // 12 bytes per entry

  // Type Palette (computed from data)
  types: string[typePaletteCount],    // Variable length strings

  // Chunk Index
  chunkIndex: [
    {
      chunkID: int16[3],              // 6 bytes (x, y, z)
      fileOffset: uint32,             // 4 bytes
      byteLength: uint32              // 4 bytes
    }
    // 14 bytes per chunk index entry
  ][chunkCount],

  // Chunk Data
  chunks: [
    {
      chunkID: int16[3],              // 6 bytes
      origin: float32[3],             // 12 bytes (world space origin)
      objectCount: uint16,            // 2 bytes

      objects: [
        {
          localPos: uint16[3],        // 6 bytes (relative to chunk origin)
          scaleIndex: uint8,          // 1 byte (255 = custom follows)
          rotationIndex: uint8,       // 1 byte (255 = custom follows)
          typeID: uint8,              // 1 byte

          // Optional: only if scaleIndex === 255
          customScale: uint16[3],     // 6 bytes (quantized)

          // Optional: only if rotationIndex === 255
          customRotation: uint16[3]   // 6 bytes (quantized radians)
        }
        // 9 bytes base
        // +6 bytes if custom scale
        // +6 bytes if custom rotation
        // Max 21 bytes, typical 9 bytes
      ][objectCount]
    }
  ][chunkCount]
}
```

---

## String serialization

- Prefix: a varint length encoded in 7-bit groups (continuation bit 0x80), little-endian.
- Payload: exactly N bytes of UTF-8 follow the varint.
- Empty string: varint zero (0x00) with no payload bytes.
- Arrays of strings are represented by the count field (e.g., `typePaletteCount`) followed by that many length-prefixed strings.

---

## Binary Layout Example

```
Offset | Size | Field
-------|------|------------------
0      | 2    | version
2      | 4    | chunkSize
6      | 24   | worldBounds (6 floats)
30     | 2    | scaleCount (e.g., 150)
32     | 1800 | scale palette (150 × 12 bytes)
1832   | 2    | rotationCount (e.g., 200)
1834   | 2400 | rotation palette (200 × 12 bytes)
4234   | 2    | chunkCount (e.g., 25)
4236   | 350  | chunk index (25 × 14 bytes)
4586   | ...  | chunk data begins
```

> Note: The exact offsets depend on the lengths of variable-length strings (mapType and type palette entries).

---

## Chunk Binary Structure

Per chunk the fields are arranged as follows:

```
Per Chunk:
Offset | Size | Field
-------|------|------------------
0      | 4    | chunkID (2 × int16)
4      | 12   | origin (3 × float32)
16     | 2    | objectCount
18     | var  | object data

Per Object (variable size):
Offset | Size | Field
-------|------|------------------
0      | 6    | localPos (3 × uint16)
6      | 1    | scaleIndex
7      | 1    | rotationIndex
8      | 1    | typeID

If scaleIndex === 255:
9      | 6    | customScale (3 × uint16)

If rotationIndex === 255:
9/15   | 6    | customRotation (3 × uint16)
```

---

## Notes and recommendations

- Keep palettes (scales, rotations, types) contiguous near the top of the file to enable quick lookups.
- Use the `chunkIndex` to locate chunk data efficiently (seek to `fileOffset` and read `byteLength`).
- Quantization details for `customScale` and `customRotation` should be documented elsewhere (how uint16 maps to float range / radians).
- When adding new fields, update the version number in the file header.

If you want, I can also:

- add a short README section showing how to parse this format in C# or TypeScript,
- or add a small test file and a parser that reads the file and prints a summary.

---

Generated: cleaned and converted from the original `binaryFormat.txt` to Markdown for readability.
