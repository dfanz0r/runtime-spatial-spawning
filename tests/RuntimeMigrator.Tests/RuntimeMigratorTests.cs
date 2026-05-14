using System.Text.Json.Nodes;
using Xunit;
using RuntimeMigrator;

namespace RuntimeMigrator.Tests;

public sealed class RuntimeMigratorTests
{
    [Fact]
    public void CustomBase16RoundTripsBytesAndValidatesInput()
    {
        byte[] bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        string encoded = Utilities.EncodeCustomBase16(bytes);
        byte[] decoded = Utilities.DecodeCustomBase16(encoded);

        Assert.Equal(bytes, decoded);
        Assert.Equal(string.Empty, Utilities.EncodeCustomBase16(Array.Empty<byte>()));
        Assert.Empty(Utilities.DecodeCustomBase16(string.Empty));
        Assert.Throws<FormatException>(() => Utilities.DecodeCustomBase16("_"));
        Assert.Throws<FormatException>(() => Utilities.DecodeCustomBase16("_!"));
        Assert.Throws<ArgumentNullException>(() => Utilities.DecodeCustomBase16(null!));
    }

    [Fact]
    public void CustomBase32RoundTripsBytesAndUsesNoLetters()
    {
        byte[] bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        string encoded = Utilities.EncodeCustomBase32(bytes);
        byte[] decoded = Utilities.DecodeCustomBase32(encoded);

        Assert.Equal(bytes, decoded);
        Assert.DoesNotContain(encoded, c => char.IsLetter(c));
        Assert.DoesNotContain('#', encoded);
        Assert.DoesNotContain(' ', encoded);
        Assert.Equal(string.Empty, Utilities.EncodeCustomBase32(Array.Empty<byte>()));
        Assert.Empty(Utilities.DecodeCustomBase32(string.Empty));
        Assert.Throws<FormatException>(() => Utilities.DecodeCustomBase32("A"));
        Assert.Throws<ArgumentNullException>(() => Utilities.DecodeCustomBase32(null!));
    }

    [Fact]
    public void VectorMathQuantizesDequantizesAndRoundsPredictably()
    {
        var v = new Vector(32, 64, -1);
        var quantized = v.Quantize(scale: 64, maxValue: 65535);
        Assert.Equal((ushort)32768, quantized.Item1);
        Assert.Equal((ushort)65535, quantized.Item2);
        Assert.Equal((ushort)0, quantized.Item3);

        var dequantized = new Vector(32768, 65535, 0).Dequantize(scale: 64, maxValue: 65535);
        AssertNear(32.00048828870069, dequantized.X, 1e-12);
        AssertNear(64, dequantized.Y, 1e-12);
        AssertNear(0, dequantized.Z, 1e-12);

        var rotation = new Vector(-Math.PI, 0, Math.PI);
        var quantizedRotation = rotation.QuantizeRotation(range: Math.PI * 2, offset: Math.PI, maxValue: 65535);
        Assert.Equal((ushort)0, quantizedRotation.Item1);
        Assert.Equal((ushort)32768, quantizedRotation.Item2);
        Assert.Equal((ushort)65535, quantizedRotation.Item3);

        Assert.Equal(new Vector(1.23, 2.35, -3.46), Vector.Round(new Vector(1.234, 2.345, -3.456), 2));
        AssertNear(13, new Vector(3, 4, 12).Magnitude(), 1e-12);
    }

    [Fact]
    public void MatrixToAnglesRadiansHandlesIdentityAndSimpleXRotation()
    {
        var identity = CodeGenerator.MatrixToAnglesRadians(
            right: new Vector(1, 0, 0),
            up: new Vector(0, 1, 0),
            front: new Vector(0, 0, 1),
            scale: new Vector(1, 1, 1));

        AssertVectorNear(new Vector(0, 0, 0), identity, 1e-12);

        double theta = Math.PI / 6;
        var xRotation = CodeGenerator.MatrixToAnglesRadians(
            right: new Vector(1, 0, 0),
            up: new Vector(0, Math.Cos(theta), Math.Sin(theta)),
            front: new Vector(0, -Math.Sin(theta), Math.Cos(theta)),
            scale: new Vector(1, 1, 1));

        AssertVectorNear(new Vector(-theta, 0, 0), xRotation, 1e-12);

        var zeroScale = CodeGenerator.MatrixToAnglesRadians(
            right: new Vector(0, 0, 0),
            up: new Vector(0, 1, 0),
            front: new Vector(0, 0, 1),
            scale: new Vector(0, 1, 1));

        AssertVectorNear(new Vector(0, 0, 0), zeroScale, 1e-12);
    }

    [Fact]
    public void ConverterWritesExpectedBinaryStringsAndFilteredJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RuntimeMigrator.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputPath = Path.Combine(tempDir, "input.spatial.json");
            string outputPath = Path.Combine(tempDir, "compiled.bin");
            File.WriteAllText(inputPath, MinimalSpatialJson);

            TextWriter originalOut = Console.Out;
            int exitCode;
            try
            {
                using var sink = new StringWriter();
                Console.SetOut(sink);
                exitCode = CodeGenerator.Main(new[] { inputPath, outputPath, "--verbose" });
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            string stringsPath = Path.Combine(tempDir, "compiled.strings.json");
            string filteredPath = Path.Combine(tempDir, "compiled_filtered.spatial.json");

            Assert.True(File.Exists(outputPath), "Verbose conversion should write the raw binary file.");
            Assert.True(File.Exists(stringsPath), "Conversion should write encoded strings JSON.");
            Assert.True(File.Exists(filteredPath), "Conversion should write filtered spatial JSON.");

            byte[] rawBinary = File.ReadAllBytes(outputPath);
            byte[] encodedBinary = ReadEncodedBinary(stringsPath);
            Assert.Equal(rawBinary, encodedBinary);

            AssertCompiledBinary(rawBinary);
            AssertFilteredJson(filteredPath);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SafeBase32FlagWritesLetterlessStringsJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RuntimeMigrator.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputPath = Path.Combine(tempDir, "input.spatial.json");
            string outputPath = Path.Combine(tempDir, "compiled.bin");
            File.WriteAllText(inputPath, MinimalSpatialJson);

            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;
            int exitCode;
            string consoleOutput;
            try
            {
                using var outSink = new StringWriter();
                using var errSink = new StringWriter();
                Console.SetOut(outSink);
                Console.SetError(errSink);
                exitCode = CodeGenerator.Main(new[] { inputPath, outputPath, "--verbose", "--safe-base32" });
                consoleOutput = outSink.ToString() + errSink.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }

            Assert.Equal(0, exitCode);
            Assert.Contains("safe-base32", consoleOutput);

            string stringsPath = Path.Combine(tempDir, "compiled.strings.json");
            string encodedText = ReadEncodedText(stringsPath);
            Assert.DoesNotContain(encodedText, c => char.IsLetter(c));
            Assert.DoesNotContain('#', encodedText);
            Assert.DoesNotContain(' ', encodedText);

            byte[] rawBinary = File.ReadAllBytes(outputPath);
            byte[] encodedBinary = Utilities.DecodeCustomBase32(encodedText);
            Assert.Equal(rawBinary, encodedBinary);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Base64FlagWritesBase64StringsJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RuntimeMigrator.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputPath = Path.Combine(tempDir, "input.spatial.json");
            string outputPath = Path.Combine(tempDir, "compiled.bin");
            File.WriteAllText(inputPath, MinimalSpatialJson);

            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;
            int exitCode;
            string consoleOutput;
            try
            {
                using var outSink = new StringWriter();
                using var errSink = new StringWriter();
                Console.SetOut(outSink);
                Console.SetError(errSink);
                exitCode = CodeGenerator.Main(new[] { inputPath, outputPath, "--verbose", "--base64" });
                consoleOutput = outSink.ToString() + errSink.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }

            Assert.Equal(0, exitCode);
            Assert.Contains("base64", consoleOutput);

            string stringsPath = Path.Combine(tempDir, "compiled.strings.json");
            byte[] rawBinary = File.ReadAllBytes(outputPath);
            byte[] encodedBinary = ReadBase64EncodedBinary(stringsPath);
            Assert.Equal(rawBinary, encodedBinary);

            Assert.Throws<FormatException>(() => Utilities.DecodeCustomBase16(ReadEncodedText(stringsPath)));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static byte[] ReadEncodedBinary(string stringsPath)
    {
        return Utilities.DecodeCustomBase16(ReadEncodedText(stringsPath));
    }

    private static byte[] ReadBase64EncodedBinary(string stringsPath)
    {
        return Convert.FromBase64String(ReadEncodedText(stringsPath));
    }

    private static string ReadEncodedText(string stringsPath)
    {
        JsonObject chunks = JsonNode.Parse(File.ReadAllText(stringsPath))!.AsObject();
        return string.Concat(chunks
            .Select(pair => new { Key = pair.Key, Value = pair.Value!.GetValue<string>() })
            .OrderBy(pair => Convert.ToInt32(pair.Key[1..], 16))
            .Select(pair => pair.Value));
    }

    private static void AssertCompiledBinary(byte[] binary)
    {
        using var reader = new BinaryFormatReader(binary);

        Assert.Equal((ushort)1, reader.Version);
        Assert.Equal(64f, reader.ChunkSize);
        Assert.Equal("Capstone", reader.MapType);
        Assert.Equal(2, reader.TotalObjects);
        Assert.Equal((ushort)2, reader.ChunkCount);

        AssertVectorNear(new Vector(1, 0, 0), reader.MinBounds, 1e-6);
        AssertVectorNear(new Vector(65, 2, 3), reader.MaxBounds, 1e-6);

        Assert.Equal((ushort)2, reader.ScalePaletteCount);
        Assert.Equal((ushort)1, reader.RotationPaletteCount);
        Assert.Equal((ushort)1, reader.TypePaletteCount);

        AssertVectorNear(new Vector(1, 1, 1), reader.ScalePalette[0], 1e-6);
        AssertVectorNear(new Vector(2, 3, 4), reader.ScalePalette[1], 1e-6);
        AssertVectorNear(new Vector(0, 0, 0), reader.RotationPalette[0], 1e-6);
        Assert.Equal("MP_Capstone_Block", reader.TypePalette[0]);

        // Chunks are sorted by (x,y,z) for determinism
        Assert.Equal(2, reader.Chunks.Count);
        AssertChunk(reader.Chunks[0], 0, 0, 0, 12);
        AssertChunk(reader.Chunks[1], 1, 0, 0, 12);

        var objs0 = reader.ReadChunkObjects(reader.Chunks[0]);
        Assert.Single(objs0);
        Assert.Equal((1024, 2048, 3072), ((ushort)objs0[0].LocalPos.X, (ushort)objs0[0].LocalPos.Y, (ushort)objs0[0].LocalPos.Z));
        Assert.Equal(0, objs0[0].ScaleIndex);
        Assert.Equal(0, objs0[0].RotationIndex);
        Assert.Equal((ushort)0, objs0[0].TypeId);

        var objs1 = reader.ReadChunkObjects(reader.Chunks[1]);
        Assert.Single(objs1);
        Assert.Equal((1024, 0, 0), ((ushort)objs1[0].LocalPos.X, (ushort)objs1[0].LocalPos.Y, (ushort)objs1[0].LocalPos.Z));
        Assert.Equal(1, objs1[0].ScaleIndex);
        Assert.Equal(0, objs1[0].RotationIndex);
        Assert.Equal((ushort)0, objs1[0].TypeId);
    }

    private static void AssertChunk(BinaryFormatReader.ChunkEntry chunk, short expectedX, short expectedY, short expectedZ, uint? expectedLength = null)
    {
        Assert.Equal(expectedX, chunk.X);
        Assert.Equal(expectedY, chunk.Y);
        Assert.Equal(expectedZ, chunk.Z);
        if (expectedLength.HasValue)
            Assert.Equal(expectedLength.Value, chunk.Length);
    }


    private static void AssertFilteredJson(string filteredPath)
    {
        JsonObject filtered = JsonNode.Parse(File.ReadAllText(filteredPath))!.AsObject();
        var portalDynamic = filtered["Portal_Dynamic"]!.AsArray();
        var statics = filtered["Static"]!.AsArray();

        Assert.Equal(2, portalDynamic.Count);
        Assert.Empty(statics);

        var retainedIds = portalDynamic.Select(node => node!["id"]!.GetValue<string>()).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { "dyn-controller", "dyn-target" }, retainedIds);
    }

    private static readonly string MinimalSpatialJson = """
    {
      "Portal_Dynamic": [
        {
          "name": "simple dynamic",
          "type": "MP_Capstone_Block",
          "position": { "x": 1, "y": 2, "z": 3 },
          "right": { "x": 1, "y": 0, "z": 0 },
          "up": { "x": 0, "y": 1, "z": 0 },
          "front": { "x": 0, "y": 0, "z": 1 },
          "id": "dyn-simple"
        },
        {
          "name": "controller",
          "type": "MP_Capstone_Block",
          "position": { "x": 10, "y": 0, "z": 0 },
          "right": { "x": 1, "y": 0, "z": 0 },
          "up": { "x": 0, "y": 1, "z": 0 },
          "front": { "x": 0, "y": 0, "z": 1 },
          "id": "dyn-controller",
          "linked": ["target"],
          "target": "dyn-target",
          "custom": true
        },
        {
          "name": "referenced target",
          "type": "MP_Capstone_Block",
          "position": { "x": 20, "y": 0, "z": 0 },
          "right": { "x": 1, "y": 0, "z": 0 },
          "up": { "x": 0, "y": 1, "z": 0 },
          "front": { "x": 0, "y": 0, "z": 1 },
          "id": "dyn-target"
        }
      ],
      "Static": [
        {
          "name": "simple static",
          "type": "MP_Capstone_Block",
          "position": { "x": 65, "y": 0, "z": 0 },
          "right": { "x": 2, "y": 0, "z": 0 },
          "up": { "x": 0, "y": 3, "z": 0 },
          "front": { "x": 0, "y": 0, "z": 4 },
          "id": "static-simple"
        }
      ]
    }
    """;

    [Fact]
    public void NegativeChunkCoordinatesProduceCorrectChunkIndex()
    {
        // Multiple objects at various negative positions
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "neg-x",
              "type": "MP_Capstone_Block",
              "position": { "x": -1, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "neg-x"
            },
            {
              "name": "neg-y",
              "type": "MP_Capstone_Block",
              "position": { "x": 0, "y": -65, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "neg-y"
            },
            {
              "name": "neg-z",
              "type": "MP_Capstone_Block",
              "position": { "x": 0, "y": 0, "z": -129 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "neg-z"
            }
          ],
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)3, chunkCount);

        var chunks = ReadChunkIndex(reader, chunkCount);

        // Sort by (x,y,z) for deterministic assertion
        chunks.Sort((a, b) =>
        {
            int cmp = a.X.CompareTo(b.X);
            if (cmp != 0) return cmp;
            cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.Z.CompareTo(b.Z);
        });

        AssertChunk(chunks[0], -1, 0, 0);
        AssertChunk(chunks[1], 0, -2, 0);
        AssertChunk(chunks[2], 0, 0, -3);

        // Verify each chunk has 1 object
        foreach (var chunk in chunks)
        {
            reader.BaseStream.Seek(chunk.Offset, SeekOrigin.Begin);
            ushort objCount = reader.ReadUInt16();
            Assert.Equal(1, objCount);
        }
    }

    [Fact]
    public void ObjectOnChunkBoundaryPlacedInCorrectChunk()
    {
        // Object at exactly (64, 0, 0) should be in chunk (1, 0, 0) since floor(64/64) = 1
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "boundary",
              "type": "MP_Capstone_Block",
              "position": { "x": 64, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "boundary-obj"
            }
          ],
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)1, chunkCount);

        var chunks = ReadChunkIndex(reader, chunkCount);
        AssertChunk(chunks[0], 1, 0, 0);
    }

    [Fact]
    public void ObjectWithoutIdIsCompressedIfNoExtraData()
    {
        // Object without 'id' field and without extra data should be compressible
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "no-id",
              "type": "MP_Capstone_Block",
              "position": { "x": 10, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 }
            }
          ],
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        // Should have 1 object in the binary (compressed), 0 in filtered
        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)1, chunkCount);

        // Filtered JSON should be empty
        var filtered = JsonNode.Parse(result.FilteredJson)!.AsObject();
        Assert.Empty(filtered["Portal_Dynamic"]!.AsArray());
        Assert.Empty(filtered["Static"]!.AsArray());
    }

    [Fact]
    public void ObjectWithOnlySkippedMetadataKeysIsCompressed()
    {
        // Object with only metadata/_edit_group_ etc should still be compressible
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "meta-only",
              "type": "MP_Capstone_Block",
              "position": { "x": 10, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "meta-only-obj",
              "metadata/_edit_group_": "some_group"
            }
          ],
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)1, chunkCount);

        // Should be filtered out (compressed)
        var filtered = JsonNode.Parse(result.FilteredJson)!.AsObject();
        Assert.Empty(filtered["Portal_Dynamic"]!.AsArray());
    }

    [Fact]
    public void StaticPrefixIdObjectsAreSkipped()
    {
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "static-prefix",
              "type": "MP_Capstone_Block",
              "position": { "x": 10, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "[STATIC]some_entity"
            }
          ],
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        // [STATIC] objects should be skipped entirely - not compressed, not in binary
        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)0, chunkCount);

        // Should remain in filtered output
        var filtered = JsonNode.Parse(result.FilteredJson)!.AsObject();
        Assert.Single(filtered["Portal_Dynamic"]!.AsArray());
    }

    [Fact]
    public void TerrainAndAssetsIdsAreSkipped()
    {
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [],
          "Static": [
            {
              "name": "terrain",
              "type": "MP_Capstone_Terrain",
              "position": { "x": 0, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "Static/MP_Capstone_Terrain"
            },
            {
              "name": "assets",
              "type": "MP_Capstone_Assets",
              "position": { "x": 0, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "Static/MP_Capstone_Assets"
            }
          ]
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        // Both should be skipped - not compressed, retained in filtered
        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)0, chunkCount);

        var filtered = JsonNode.Parse(result.FilteredJson)!.AsObject();
        Assert.Equal(2, filtered["Static"]!.AsArray().Count);
    }

    [Fact]
    public void LinkedStringReferenceAddsReferencedIdToFiltered()
    {
        // An object with extra data and a 'linked' -> string reference should reference another id
        // The referenced id should NOT be compressed (should remain in filtered)
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "controller",
              "type": "MP_Capstone_Block",
              "position": { "x": 10, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "controller-obj",
              "linked": ["target"],
              "target": "target-obj"
            },
            {
              "name": "target",
              "type": "MP_Capstone_Block",
              "position": { "x": 20, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "target-obj"
            }
          ],
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        // Controller has extra data -> not compressed -> should be in filtered
        // Target is referenced -> should NOT be compressed -> should be in filtered
        // So filtered should have 2 objects, binary should have 0
        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)0, chunkCount);

        var filtered = JsonNode.Parse(result.FilteredJson)!.AsObject();
        Assert.Equal(2, filtered["Portal_Dynamic"]!.AsArray().Count);

        var retainedIds = filtered["Portal_Dynamic"]!.AsArray()
            .Select(n => n!["id"]!.GetValue<string>())
            .OrderBy(id => id)
            .ToArray();
        Assert.Equal(new[] { "controller-obj", "target-obj" }, retainedIds);
    }

    [Fact]
    public void LinkedArrayReferenceAddsAllReferencedIds()
    {
        // linked property referencing a JSON array of IDs
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "multicontroller",
              "type": "MP_Capstone_Block",
              "position": { "x": 10, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "multi-ctrl",
              "linked": ["targets"],
              "targets": ["target-a", "target-b"]
            },
            {
              "name": "target-a",
              "type": "MP_Capstone_Block",
              "position": { "x": 20, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "target-a"
            },
            {
              "name": "target-b",
              "type": "MP_Capstone_Block",
              "position": { "x": 30, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "target-b"
            }
          ],
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        var filtered = JsonNode.Parse(result.FilteredJson)!.AsObject();
        Assert.Equal(3, filtered["Portal_Dynamic"]!.AsArray().Count);

        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)0, chunkCount);
    }

    [Fact]
    public void LinkedMissingOrInvalidKeyDoesNotCrash()
    {
        // linked key that doesn't exist in the object, or invalid linked value
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "bad-linked",
              "type": "MP_Capstone_Block",
              "position": { "x": 10, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "bad-linked-obj",
              "linked": ["nonexistent_key"],
              "custom": true
            },
            {
              "name": "empty-linked",
              "type": "MP_Capstone_Block",
              "position": { "x": 20, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "empty-linked-obj",
              "linked": [],
              "custom": true
            }
          ],
          "Static": []
        }
        """;

        // Should not throw
        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);

        // Both have extra data (custom: true) so they should be in filtered, not compressed
        var filtered = JsonNode.Parse(result.FilteredJson)!.AsObject();
        Assert.Equal(2, filtered["Portal_Dynamic"]!.AsArray().Count);

        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        ReadHeader(reader, out var chunkCount);
        Assert.Equal((ushort)0, chunkCount);
    }

    [Fact]
    public void NonVerboseModeDoesNotWriteRawBinary()
    {
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "simple",
              "type": "MP_Capstone_Block",
              "position": { "x": 10, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "simple-obj"
            }
          ],
          "Static": []
        }
        """;

        string tempDir = Path.Combine(Path.GetTempPath(), "RuntimeMigrator.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string inputPath = Path.Combine(tempDir, "input.spatial.json");
            string outputPath = Path.Combine(tempDir, "compiled.bin");
            File.WriteAllText(inputPath, json);

            // Run WITHOUT --verbose
            TextWriter originalOut = Console.Out;
            try
            {
                using var sink = new StringWriter();
                Console.SetOut(sink);
                CodeGenerator.Main(new[] { inputPath, outputPath });
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            // Raw binary should NOT be written in non-verbose mode
            Assert.False(File.Exists(outputPath), "Non-verbose mode should not write raw binary.");

            // Strings and filtered should still be written
            string stringsPath = Path.Combine(tempDir, "compiled.strings.json");
            string filteredPath = Path.Combine(tempDir, "compiled_filtered.spatial.json");
            Assert.True(File.Exists(stringsPath));
            Assert.True(File.Exists(filteredPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void EmptyObjectListsProduceNoChunks()
    {
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [],
          "Static": []
        }
        """;

        // Should handle empty gracefully - no objects found
        using var result = RunConverter(json, "--verbose");

        // Console output should mention no objects
        Assert.Contains("No objects found", result.ConsoleOutput);
        Assert.Equal(3, result.ExitCode);

        // The converter returns early, so no output files expected
        Assert.Null(result.RawBinary);
    }

    [Fact]
    public void MissingPortalDynamicReportsError()
    {
        string json = /*lang=json,strict*/ """
        {
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.Contains("Missing required section", result.ConsoleOutput);
        Assert.Contains("Portal_Dynamic", result.ConsoleOutput);
    }

    [Fact]
    public void MissingStaticReportsError()
    {
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.Contains("Missing required section", result.ConsoleOutput);
        Assert.Contains("Static", result.ConsoleOutput);
    }

    [Fact]
    public void MalformedJsonReportsError()
    {
        string json = "{ this is not valid json }";
        using var result = RunConverter(json, "--verbose");
        Assert.Contains("Error", result.ConsoleOutput);
        Assert.Contains("Invalid JSON", result.ConsoleOutput);
        // No output files should be produced
        Assert.Null(result.RawBinary);
    }

    [Fact]
    public void ChunkCountExceedingUshortMaxReportsError()
    {
        int chunkLimit = ushort.MaxValue + 1;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"Portal_Dynamic\":[");
        for (int i = 0; i < chunkLimit; i++)
        {
            if (i > 0) sb.Append(",");
            double x = i * 64.0;
            sb.Append("{\"name\":\"c\"")
              .Append(",\"type\":\"MP_Capstone_Block\"")
              .Append(",\"position\":{\"x\":").Append(x).Append(",\"y\":0,\"z\":0}")
              .Append(",\"right\":{\"x\":1,\"y\":0,\"z\":0}")
              .Append(",\"up\":{\"x\":0,\"y\":1,\"z\":0}")
              .Append(",\"front\":{\"x\":0,\"y\":0,\"z\":1}")
              .Append(",\"id\":\"c-").Append(i).Append("\"}");
        }
        sb.Append("],\"Static\":[]}");

        using var result = RunConverter(sb.ToString(), "--verbose");
        Assert.Contains("Chunk count", result.ConsoleOutput);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void TypePaletteExceedingUshortMaxReportsError()
    {
        int typeLimit = ushort.MaxValue + 1;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"Portal_Dynamic\":[");
        for (int i = 0; i < typeLimit; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\"t\"")
              .Append(",\"type\":\"Type_")
              .Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append("\"")
              .Append(",\"position\":{\"x\":0,\"y\":0,\"z\":0}")
              .Append(",\"right\":{\"x\":1,\"y\":0,\"z\":0}")
              .Append(",\"up\":{\"x\":0,\"y\":1,\"z\":0}")
              .Append(",\"front\":{\"x\":0,\"y\":0,\"z\":1}")
              .Append(",\"id\":\"t-").Append(i).Append("\"}");
        }
        sb.Append("],\"Static\":[]}");

        using var result = RunConverter(sb.ToString(), "--verbose");
        Assert.Contains("Type palette count", result.ConsoleOutput);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void ChunkCoordinateExceedingShortRangeReportsError()
    {
        string json = /*lang=json,strict*/ """
        {
          "Portal_Dynamic": [
            {
              "name": "far",
              "type": "MP_Capstone_Block",
              "position": { "x": 5000000, "y": 0, "z": 0 },
              "right": { "x": 1, "y": 0, "z": 0 },
              "up": { "x": 0, "y": 1, "z": 0 },
              "front": { "x": 0, "y": 0, "z": 1 },
              "id": "far-obj"
            }
          ],
          "Static": []
        }
        """;

        using var result = RunConverter(json, "--verbose");
        Assert.Contains("Chunk coordinate", result.ConsoleOutput);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void PerChunkObjectCountExceedingUshortMaxReportsError()
    {
        // All objects at the same position so they land in a single chunk
        int objCount = ushort.MaxValue + 1;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"Portal_Dynamic\":[");
        for (int i = 0; i < objCount; i++)
        {
            if (i > 0) sb.Append(",");
            // Vary type name so each object is distinct (needed for unique ids/compressible tracking)
            sb.Append("{\"name\":\"o\"")
              .Append(",\"type\":\"MP_Capstone_Block\"")
              .Append(",\"position\":{\"x\":0,\"y\":0,\"z\":0}")
              .Append(",\"right\":{\"x\":1,\"y\":0,\"z\":0}")
              .Append(",\"up\":{\"x\":0,\"y\":1,\"z\":0}")
              .Append(",\"front\":{\"x\":0,\"y\":0,\"z\":1}")
              .Append(",\"id\":\"o-").Append(i).Append("\"}");
        }
        sb.Append("],\"Static\":[]}");

        using var result = RunConverter(sb.ToString(), "--verbose");
        Assert.Contains("Object count", result.ConsoleOutput);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void TypePaletteCountExceedingUshortMaxThrows()
    {
        var typePalette = Enumerable.Range(0, ushort.MaxValue + 1)
            .Select(i => $"Type_{i}")
            .ToList();

        var data = new SpatialObjectClassifier.ClassificationResult
        {
            CompressibleObjects = new List<SpatialObjectClassifier.CompressibleObject>(),
            CompressibleIds = new HashSet<string>(),
            ScalePalette = new List<Vector>(),
            RotationPalette = new List<Vector>(),
            TypePalette = typePalette,
            ScaleIndex = new Dictionary<Vector, int>(),
            RotationIndex = new Dictionary<Vector, int>(),
            TypeNameToIndex = new Dictionary<string, int>(),
            MapType = "Test",
            MinBounds = new Vector(0, 0, 0),
            MaxBounds = new Vector(0, 0, 0),
            DroppedScaleCount = 0,
            DroppedRotationCount = 0
        };

        var ex = Assert.Throws<InvalidOperationException>(() => SpatialBinaryWriter.Write(data));
        Assert.Contains("Type palette count", ex.Message);
    }

    [Fact]
    public void RepeatedConversionsProduceIdenticalOutput()
    {
        // Run twice on the same input; binary and strings must be byte-identical.
        byte[] firstBinary;
        string firstStrings;
        string firstFiltered;

        using (var r1 = RunConverter(MinimalSpatialJson, "--verbose"))
        {
            Assert.NotNull(r1.RawBinary);
            firstBinary = r1.RawBinary;
            firstStrings = r1.StringsJson;
            firstFiltered = r1.FilteredJson;
        }

        using (var r2 = RunConverter(MinimalSpatialJson, "--verbose"))
        {
            Assert.NotNull(r2.RawBinary);
            Assert.Equal(firstBinary, r2.RawBinary);
            Assert.Equal(firstStrings, r2.StringsJson);
            Assert.Equal(firstFiltered, r2.FilteredJson);
        }
    }

    [Fact]
    public void ScalePaletteOverflowStoresExcessScalesInline()
    {
        // Create objects with >255 unique scales; the rest use sentinel 255 + inline values
        int totalObjects = 260;
        var jsonParts = new List<string>(totalObjects + 2);
        jsonParts.Add("{\"Portal_Dynamic\":[");
        for (int i = 0; i < totalObjects; i++)
        {
            if (i > 0) jsonParts.Add(",");
            double s = 1.0 + i;
            string sStr = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
            jsonParts.Add("{\"name\":\"obj-" + i +
              "\",\"type\":\"MP_Capstone_Block\"" +
              ",\"position\":{\"x\":10,\"y\":0,\"z\":0}" +
              ",\"right\":{\"x\":" + sStr + ",\"y\":0,\"z\":0}" +
              ",\"up\":{\"x\":0,\"y\":" + sStr + ",\"z\":0}" +
              ",\"front\":{\"x\":0,\"y\":0,\"z\":" + sStr + "}" +
              ",\"id\":\"overflow-" + i + "\"}");
        }
        jsonParts.Add("],\"Static\":[]}");
        string json = string.Concat(jsonParts);

        using var result = RunConverter(json, "--verbose");
        Assert.NotNull(result.RawBinary);
        Assert.Contains("exceeded byte palette limit and will be stored inline", result.ConsoleOutput);

        using var reader = new BinaryReader(new MemoryStream(result.RawBinary));
        reader.ReadUInt16(); // version
        reader.ReadSingle();  // chunkSize
        reader.ReadString();  // mapType
        Assert.Equal(totalObjects, reader.ReadInt32());
        ushort chunkCount = reader.ReadUInt16();
        Assert.Equal((ushort)1, chunkCount);
        // world bounds (skip)
        reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
        reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();

        ushort scalePaletteCount = reader.ReadUInt16();
        ushort rotationPaletteCount = reader.ReadUInt16();
        ushort typePaletteCount = reader.ReadUInt16();

        // Max palette size is 255 (indices 0..254), since `Count <= MaxPaletteIndex` (254).
        // 260 unique scales -> 255 in palette, 5 inline.
        Assert.Equal((ushort)255, scalePaletteCount);
        // All objects have identity rotation (same scale on all axes -> normalized vectors are axis-aligned)
        Assert.Equal((ushort)1, rotationPaletteCount);
        Assert.Equal((ushort)1, typePaletteCount);

        // Skip palettes to get to chunk index
        for (int i = 0; i < scalePaletteCount; i++) { reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); }
        for (int i = 0; i < rotationPaletteCount; i++) { reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); }
        for (int i = 0; i < typePaletteCount; i++) { reader.ReadString(); }

        var chunks = ReadChunkIndex(reader, chunkCount);
        Assert.Single(chunks);

        reader.BaseStream.Seek(chunks[0].Offset, SeekOrigin.Begin);
        Assert.Equal((ushort)totalObjects, reader.ReadUInt16());

        int inlineScaleCount = 0;
        int inlineRotCount = 0;
        for (int i = 0; i < totalObjects; i++)
        {
            reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt16(); // localPos
            byte scaleIdx = reader.ReadByte();
            byte rotIdx = reader.ReadByte();
            reader.ReadUInt16(); // typeId

            if (scaleIdx == 255)
            {
                inlineScaleCount++;
                reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt16(); // custom scale
            }
            if (rotIdx == 255)
            {
                inlineRotCount++;
                reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt16(); // custom rotation
            }
        }

        // Only scales should overflow (260 unique > 255 limit)
        Assert.Equal(5, inlineScaleCount);
        Assert.Equal(0, inlineRotCount); // rotation is always identity, fits in palette
    }
    private sealed class ConversionResult : IDisposable
    {
        public string TempDir { get; }
        public string ConsoleOutput { get; }
        public byte[]? RawBinary { get; }
        public string FilteredJson { get; }
        public string StringsJson { get; }
        public int ExitCode { get; }

        public ConversionResult(string tempDir, string consoleOutput, byte[]? rawBinary, string filteredJson, string stringsJson, int exitCode = 0)
        {
            TempDir = tempDir;
            ConsoleOutput = consoleOutput;
            RawBinary = rawBinary;
            FilteredJson = filteredJson;
            StringsJson = stringsJson;
            ExitCode = exitCode;
        }

        public void Dispose()
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, recursive: true);
        }
    }

    private static ConversionResult RunConverter(string inputJson, params string[] extraArgs)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RuntimeMigrator.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string inputPath = Path.Combine(tempDir, "input.spatial.json");
        string outputPath = Path.Combine(tempDir, "compiled.bin");
        File.WriteAllText(inputPath, inputJson);

        var args = new List<string> { inputPath, outputPath };
        args.AddRange(extraArgs);

        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        string consoleOutput;
        int exitCode;
        try
        {
            using var outSink = new StringWriter();
            using var errSink = new StringWriter();
            Console.SetOut(outSink);
            Console.SetError(errSink);
            exitCode = CodeGenerator.Main(args.ToArray());
            consoleOutput = outSink.ToString() + errSink.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        string stringsPath = Path.Combine(tempDir, "compiled.strings.json");
        string filteredPath = Path.Combine(tempDir, "compiled_filtered.spatial.json");

        byte[]? rawBinary = File.Exists(outputPath) ? File.ReadAllBytes(outputPath) : null;
        string filteredJson = File.Exists(filteredPath) ? File.ReadAllText(filteredPath) : "";
        string stringsJson = File.Exists(stringsPath) ? File.ReadAllText(stringsPath) : "";

        return new ConversionResult(tempDir, consoleOutput, rawBinary, filteredJson, stringsJson, exitCode);
    }

    private static void ReadHeader(BinaryReader reader, out ushort chunkCount)
    {
        reader.ReadUInt16(); // version
        reader.ReadSingle();  // chunkSize
        reader.ReadString();  // mapType
        reader.ReadInt32();   // totalObjects
        chunkCount = reader.ReadUInt16();
        // Skip world bounds (6 floats)
        reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
        reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
        // Skip palette counts
        ushort scalePaletteCount = reader.ReadUInt16();
        ushort rotationPaletteCount = reader.ReadUInt16();
        ushort typePaletteCount = reader.ReadUInt16();
        // Skip scale palette
        for (int i = 0; i < scalePaletteCount; i++)
        {
            reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
        }
        // Skip rotation palette
        for (int i = 0; i < rotationPaletteCount; i++)
        {
            reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
        }
        // Skip type palette
        for (int i = 0; i < typePaletteCount; i++)
        {
            reader.ReadString();
        }
    }

    private static List<BinaryFormatReader.ChunkEntry> ReadChunkIndex(BinaryReader reader, ushort chunkCount)
    {
        var chunks = new List<BinaryFormatReader.ChunkEntry>();
        for (int i = 0; i < chunkCount; i++)
        {
            chunks.Add(new BinaryFormatReader.ChunkEntry(
                reader.ReadInt16(),
                reader.ReadInt16(),
                reader.ReadInt16(),
                reader.ReadUInt32(),
                reader.ReadUInt32()));
        }
        return chunks;
    }

    private static void AssertNear(double expected, double actual, double tolerance)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, actual {actual}, tolerance {tolerance}.");
    }

    private static void AssertVectorNear(Vector expected, Vector actual, double tolerance)
    {
        AssertNear(expected.X, actual.X, tolerance);
        AssertNear(expected.Y, actual.Y, tolerance);
        AssertNear(expected.Z, actual.Z, tolerance);
    }
}
