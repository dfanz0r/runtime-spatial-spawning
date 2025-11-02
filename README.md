# Runtime Spatial Spawning

This codebase implements runtime spawning of spatial objects authored offline in godot

The current implementation uses blocks to dynamically show around players a psudo terrain like surface

# What it does

This will take a spatial json file and split out most objects from it into a custom binary format that is stored inside strings.json
this allows us to design data which can then be dynamically spawned at runtime through various means.

The current code is doing a runtime spawned terrain just as a proof of concept. There are MANY issues with the current portal runtime causing bugs that make this tricky including:

- ~4096 active runtime object limit, past this the game starts to freak out and cause game/server crashing
- Runtime memory limit, the game runtime will kill the typescript code execution if the memory gets too large. So spatial design does still need to keep that in mind.
- Runtime code iteration limit, if the code executes too long without returning control to the game the script will also be killed.

Building this code has had to walk a fine line between all of these constraints to build what is here thus far. I hope that in the future the game will work to fix these issues and increase these limits overall.

# Usage

In order to use this tool ensure you have the latest .net sdk installed, then you can use the following command to compile spatial data for use in strings.json
`dotnet run -c Release ..\MP_Capstone.spatial.json`

```
Loading JSON
Total objects found: 4780
Map Type: Capstone
Found 11 referenced objects from incompressible objects.
Found 4762 objects to compress.
Encoded Object Count: 4762 Chunk Count: 125
Scale Palette Count: 2 Rotation Palette Count: 4762 Type Palette Count: 2
Objects included in binary: 4762
Encoded JSON written to ..\MP_Capstone.strings.json
Filtered JSON written to ..\MP_Capstone_filtered.spatial.json
```

This will produce 2 new files:

MP_Capstone.strings.json - this contains all spatial data that does not have special configuration inside the original source file
MP_Capstone_filtered.spatial.json - this has all of the objects that were checked and required to stay inside the spatial json file

# runtime-code

The code required to test this is located in the `runtime-code` folder. You just need to upload the file to the portal website as-is and then you can use whatever spatial data you want in the strings file that was compiled through this tool.
