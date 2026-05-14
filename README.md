# Runtime Spatial Spawning

This codebase implements runtime spawning of spatial objects authored offline in Godot.

The current implementation converts a spatial JSON file into a custom binary format stored in `.strings.json`, which can be dynamically spawned at runtime through the Portal runtime code.

The current code is doing a runtime spawned terrain just as a proof of concept. There are MANY issues with the current portal runtime causing bugs that make this tricky including:

- ~4096 active runtime object limit, past this the game starts to freak out and cause game/server crashing
- Runtime memory limit, the game runtime will kill the typescript code execution if the memory gets too large. So spatial design does still need to keep that in mind.
- Runtime code iteration limit, if the code executes too long without returning control to the game the script will also be killed.

Building this code has had to walk a fine line between all of these constraints to build what is here thus far. I hope that in the future the game will work to fix these issues and increase these limits overall.

# Usage

In order to use this tool ensure you have the latest .NET SDK installed, then you can use the following command:

```
dotnet run -c Release --project portal-migrator <input.spatial.json> [output.bin] [--verbose]
```

| Argument | Description |
|----------|-------------|
| `<input.spatial.json>` | Path to the source spatial JSON file (required) |
| `[output.bin]` | Output path for the raw binary file. Default: `output.bin` |
| `--verbose` | Also write the raw binary file (without this flag, only `.strings.json` and `_filtered.spatial.json` are written) |

Exit codes:

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Usage error (missing args) |
| 2 | Input file not found |
| 3 | Invalid operation (e.g., missing required JSON sections, no objects) |
| 4 | I/O error during write |
| 5 | Invalid JSON |
| 6 | Unexpected error |

### Example

```
dotnet run -c Release --project portal-migrator ..\MP_Capstone.spatial.json output.bin --verbose
```

Output:
```
Loading JSON
Warning: 4507 unique rotation(s) exceeded byte palette limit and will be stored inline.
Map Type: Capstone
Total objects found: 4780
Objects included in binary: 4762
Encoded Object Count: 4762 Chunk Count: 237
Scale Palette Count: 2 Rotation Palette Count: 255 Type Palette Count: 2
Binary file written to output.bin
Encoded JSON written to output.strings.json
Filtered JSON written to output_filtered.spatial.json
```

### Output files

| File | Description |
|------|-------------|
| `output.strings.json` | Binary spatial data encoded in custom base16, split into 200-character chunks (`A0`, `A1`, ...). Read at runtime by `runtimeSpawn.ts`. |
| `output_filtered.spatial.json` | Retained objects (incompressible — those with extra data, linked references, or explicitly skipped). These are authored manually in the spatial JSON and not handled by the binary system. |

### Object classification

Objects are classified during conversion:

- **Compressible** → stored in the binary format. Objects without extra data (beyond `name`, `type`, `position`, `right`, `up`, `front`, `id`) and not referenced by any incompressible object.
- **Retained** → stay in the filtered spatial JSON. Objects with extra data, linked references, `[STATIC]` prefix ids, or terrain/assets skip ids.

If more than 254 unique scales or rotations exist, the excess use inline storage (palette sentinel 255) in the binary format.

# runtime-code

The Portal runtime scripts are located in the `runtime-code` folder.

- `runtimeSpawn.ts` is the minimal static spawner. Upload this script with the generated `.strings.json` file to parse the RuntimeMigrator binary stream and spawn every encoded object once.
- `terrainExperience.ts` is the older/full proof-of-concept experience script with game mode logic and dynamic terrain ownership behavior.

# Binary format

See [BinaryFormat.md](./BinaryFormat.md) for the full binary layout specification.

# Project structure

| File | Purpose |
|------|---------|
| `Program.cs` | CLI entry point, parses args, delegates to `SpatialMigrator` |
| `SpatialMigrator.cs` | Orchestrates JSON-to-binary conversion workflow |
| `SpatialObjectClassifier.cs` | Classifies compressible vs retained objects, builds palettes |
| `SpatialBinaryWriter.cs` | Writes the custom binary format to a byte array |
| `StringsJsonWriter.cs` | Encodes binary data as custom-base16 chunked JSON |
| `FilteredSpatialJsonWriter.cs` | Writes retained spatial JSON for non-compressible objects |
| `Vector.cs` | 3D vector math, quantization/dequantization |
| `Utilities.cs` | Custom base16 encoding/decoding |

# godot

If you want to mess around with the terrain generator script copy the levels/scripts folders into your portal SDK GodotProject folder
