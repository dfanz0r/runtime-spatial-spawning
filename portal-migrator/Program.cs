// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Linq;

public record SpatialObject(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("position")] Vector Position,
    [property: JsonPropertyName("right")] Vector Right,
    [property: JsonPropertyName("up")] Vector Up,
    [property: JsonPropertyName("front")] Vector Front,
    [property: JsonPropertyName("id")] string Id
);

public class CodeGenerator
{
    private const float ChunkSize = 64.0f;
    private const float MaxUint16 = 65535.0f;
    private const float ScaleMax = 100.0f;
    private const float RotationRange = (float)Math.PI * 2;

    public static void Main(string[] args)
    {
        bool verbose = false;
        var argList = new List<string>(args);
        if (argList.Contains("--verbose"))
        {
            verbose = true;
            argList.Remove("--verbose");
        }

        if (argList.Count < 1)
        {
            Console.WriteLine("Usage: RuntimeMigrator <input.json> [output.bin] [--verbose]");
            Console.WriteLine("Or: RuntimeMigrator <input.bin> <output.json>");
            return;
        }

        string inputPath = argList[0];

        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"Error: File not found at path '{Path.GetFullPath(inputPath)}'");
            return;
        }

        // Forward mode: JSON to binary
        string outputPath = argList.Count > 1 ? argList[1] : "output.bin";
        ConvertJsonToBinary(inputPath, outputPath, verbose);
    }

    private static void ConvertJsonToBinary(string inputPath, string outputPath, bool verbose)
    {
        string jsonContent = File.ReadAllText(inputPath);
        var rootDoc = JsonDocument.Parse(jsonContent);

        var portalDynamicElement = rootDoc.RootElement.GetProperty("Portal_Dynamic");
        var staticElement = rootDoc.RootElement.GetProperty("Static");

        Console.WriteLine("Loading JSON");
        // Collect all objects as JsonElement with type
        var allObjects = new List<JsonElement>();
        foreach (var obj in portalDynamicElement.EnumerateArray())
        {
            allObjects.Add(obj);
        }
        foreach (var obj in staticElement.EnumerateArray())
        {
            allObjects.Add(obj);
        }

        if (allObjects.Count == 0)
        {
            Console.WriteLine("Error: No objects found in JSON");
            return;
        }

        Console.WriteLine($"Total objects found: {allObjects.Count}");

        // Find map type from Static objects
        string mapType = "Unknown";
        if (staticElement.GetArrayLength() > 0)
        {
            var firstStatic = staticElement[0];
            if (firstStatic.TryGetProperty("type", out var typeProp))
            {
                string type = typeProp.GetString() ?? "";
                var parts = type.Split('_');
                if (parts.Length >= 3 && parts[0] == "MP")
                {
                    mapType = parts[1];
                }
            }
        }
        Console.WriteLine($"Map Type: {mapType}");

        // Compute world bounds
        var minBounds = new Vector { X = float.MaxValue, Y = float.MaxValue, Z = float.MaxValue };
        var maxBounds = new Vector { X = float.MinValue, Y = float.MinValue, Z = float.MinValue };

        var validObjects = new List<(SpatialObject obj, Vector scale, Vector rotation, string typeName)>();
        var compressedObjects = new HashSet<JsonElement>();

        var baseKeys = new HashSet<string> { "name", "type", "position", "right", "up", "front", "id" };

        var skipIds = new HashSet<string>() { $"Static/MP_{mapType}_Terrain", $"Static/MP_{mapType}_Assets" };
        var skipNames = new HashSet<string>() { "metadata/_edit_group_", "metadata/organizer_group", "metadata/organizer_symbol", "metadata/organizer_color" };

        var idToElement = new Dictionary<string, JsonElement>();
        // Remove from compressedObjects any that are referenced by incompressible objects
        var referencedIds = new HashSet<string>();

        foreach (var objElement in allObjects)
        {
            // Build id to element map
            var hasIdProp = objElement.TryGetProperty("id", out var idProp);
            string? id = hasIdProp ? idProp.GetString() : null;

            if (id != null && (skipIds.Contains(id) || id.StartsWith("[STATIC]")))
                continue;

            if (!string.IsNullOrEmpty(id))
            {
                idToElement[id] = objElement;
            }

            // Check for extra properties
            bool hasExtraData = false;
            foreach (var prop in objElement.EnumerateObject())
            {
                if (!baseKeys.Contains(prop.Name) && !skipNames.Contains(prop.Name))
                {
                    hasExtraData = true;
                    break;
                }
            }

            if (hasExtraData)
            {
                if (objElement.TryGetProperty("linked", out var linkedProp) && linkedProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var linkedItem in linkedProp.EnumerateArray())
                    {
                        if (linkedItem.ValueKind != JsonValueKind.String)
                            continue;

                        string? linkedKey = linkedItem.GetString();

                        if (string.IsNullOrEmpty(linkedKey))
                            continue;

                        // Look up the property in the current objElement
                        if (objElement.TryGetProperty(linkedKey, out var refProp))
                        {
                            if (refProp.ValueKind == JsonValueKind.String)
                            {
                                string? refId = refProp.GetString();
                                if (!string.IsNullOrEmpty(refId))
                                {
                                    referencedIds.Add(refId);
                                }
                            }
                            else if (refProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var refItem in refProp.EnumerateArray())
                                {
                                    if (refItem.ValueKind == JsonValueKind.String)
                                    {
                                        string? refId = refItem.GetString();
                                        if (!string.IsNullOrEmpty(refId))
                                        {
                                            referencedIds.Add(refId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                compressedObjects.Add(objElement);
            }
        }

        Console.WriteLine($"Found {referencedIds.Count} referenced objects from incompressible objects.");

        // Remove referenced from compressed
        foreach (var refId in referencedIds)
        {
            if (idToElement.TryGetValue(refId, out var refElement))
            {
                compressedObjects.Remove(refElement);
            }
        }

        Console.WriteLine($"Found {compressedObjects.Count} objects to compress.");

        // Now build validObjects from compressedObjects
        foreach (var objElement in compressedObjects)
        {
            // Deserialize to SpatialObject
            var obj = objElement.Deserialize<SpatialObject>();
            if (obj == null || obj.Type == null || obj.Position == null || obj.Right == null || obj.Up == null || obj.Front == null)
                continue;

            Vector position = obj.Position;
            var scale = new Vector
            {
                X = obj.Right.Magnitude(),
                Y = obj.Up.Magnitude(),
                Z = obj.Front.Magnitude()
            };
            Vector rotation = MatrixToAnglesRadians(obj.Right, obj.Up, obj.Front, scale);

            // Console.WriteLine($"Debug: Position x={position.X}, y={position.Y}, z={position.Z} -  Pitch={RadiansToDegrees(rotation.X)}, Yaw={RadiansToDegrees(rotation.Y)}, Roll={RadiansToDegrees(rotation.Z)}");

            validObjects.Add((obj, Vector.Round(scale, 2), rotation, obj.Type));

            minBounds = Vector.Min(minBounds, position);
            maxBounds = Vector.Max(maxBounds, position);
        }

        // Build palettes
        var scalePalette = new List<Vector>();
        var scaleIndex = new Dictionary<Vector, int>();
        var rotationPalette = new List<Vector>();
        var rotationIndex = new Dictionary<Vector, int>();
        var typePalette = new List<string>();
        var typeNameToIndex = new Dictionary<string, int>();

        foreach (var (_, scale, rotation, typeName) in validObjects)
        {
            if (!scaleIndex.ContainsKey(scale))
            {
                scaleIndex[scale] = scalePalette.Count;
                scalePalette.Add(scale);
            }
            if (!rotationIndex.ContainsKey(rotation))
            {
                rotationIndex[rotation] = rotationPalette.Count;
                rotationPalette.Add(rotation);
            }
            if (!typeNameToIndex.ContainsKey(typeName))
            {
                typeNameToIndex[typeName] = typePalette.Count;
                typePalette.Add(typeName);
            }
        }

        // Group into chunks
        var chunks = new Dictionary<(int x, int y, int z), List<(Vector position, Vector scale, Vector rotation, int typeIndex)>>();

        foreach (var item in validObjects)
        {
            int cx = (int)Math.Floor(item.obj.Position.X / ChunkSize);
            int cy = (int)Math.Floor(item.obj.Position.Y / ChunkSize);
            int cz = (int)Math.Floor(item.obj.Position.Z / ChunkSize);
            var key = (cx, cy, cz);

            if (!chunks.ContainsKey(key))
                chunks[key] = [];

            int typeIndexValue = typeNameToIndex[item.typeName];
            chunks[key].Add((item.obj.Position, item.scale, item.rotation, typeIndexValue));
        }

        // Write binary
        byte[] binaryData;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            // Header
            writer.Write((ushort)1); // format version
            writer.Write(ChunkSize);
            writer.Write(mapType);

            Console.WriteLine($"Encoded Object Count: {validObjects.Count} Chunk Count: {chunks.Count}");
            writer.Write(validObjects.Count); // useful for pre-allocating arrays on readers
            writer.Write((ushort)chunks.Count);

            // Object bounds min/max
            minBounds.WriteToAsFloat(writer);
            maxBounds.WriteToAsFloat(writer);

            Console.WriteLine($"Scale Palette Count: {scalePalette.Count} Rotation Palette Count: {rotationPalette.Count} Type Palette Count: {typePalette.Count}");
            writer.Write((ushort)scalePalette.Count);
            writer.Write((ushort)rotationPalette.Count);
            writer.Write((ushort)typePalette.Count);

            // Scale palette
            foreach (var s in scalePalette)
            {
                s.WriteToAsFloat(writer);
            }

            // Rotation palette
            foreach (var r in rotationPalette)
            {
                r.WriteToAsFloat(writer);
            }

            // Type palette
            foreach (var t in typePalette)
            {
                writer.Write(t);
            }

            // Chunk index (dummy for now)
            long chunkIndexPos = ms.Position;
            var chunkKeys = chunks.Keys.ToList();
            for (int i = 0; i < chunks.Count; i++)
            {
                writer.Write((short)0);
                writer.Write((short)0);
                writer.Write((short)0);
                writer.Write((uint)0);
                writer.Write((uint)0);
            }

            // Chunk data
            var chunkOffsets = new List<long>();
            var chunkLengths = new List<uint>();

            foreach (var key in chunkKeys)
            {
                var (cx, cy, cz) = key;

                long startPos = ms.Position;
                chunkOffsets.Add(startPos);

                Vector origin = new() { X = cx * ChunkSize, Y = cy * ChunkSize, Z = cz * ChunkSize };

                var objs = chunks[key];
                writer.Write((ushort)objs.Count);

                foreach (var (pos, scale, rotation, typeId) in objs)
                {
                    Vector localPos = new()
                    {
                        X = pos.X - origin.X,
                        Y = pos.Y - origin.Y,
                        Z = pos.Z - origin.Z
                    };

                    // Quantize localPos
                    var (px, py, pz) = localPos.Quantize(ChunkSize, MaxUint16);
                    writer.Write(px);
                    writer.Write(py);
                    writer.Write(pz);

                    int scaleIdx = scaleIndex[scale];
                    bool customScale = scaleIdx >= 255;
                    byte scaleIdxByte = customScale ? (byte)255 : (byte)scaleIdx;
                    writer.Write(scaleIdxByte);

                    int rotIdx = rotationIndex[rotation];
                    bool customRot = rotIdx >= 255;
                    byte rotIdxByte = customRot ? (byte)255 : (byte)rotIdx;
                    writer.Write(rotIdxByte);

                    writer.Write((ushort)typeId);

                    if (customScale)
                    {
                        // Quantize custom scale
                        var (sx, sy, sz) = scale.Quantize(ScaleMax, MaxUint16);

                        writer.Write(sx);
                        writer.Write(sy);
                        writer.Write(sz);
                    }

                    if (customRot)
                    {
                        // Quantize custom rotation (radians to 0-65535 for -pi to pi)
                        var (rx, ry, rz) = rotation.QuantizeRotation(RotationRange, Math.PI, MaxUint16);

                        writer.Write(rx);
                        writer.Write(ry);
                        writer.Write(rz);
                    }
                }

                long endPos = ms.Position;
                chunkLengths.Add((uint)(endPos - startPos));
            }

            // Go back and write chunk index
            ms.Seek(chunkIndexPos, SeekOrigin.Begin);
            for (int i = 0; i < chunkKeys.Count; i++)
            {
                var (cx, cy, cz) = chunkKeys[i];

                writer.Write((short)cx);
                writer.Write((short)cy);
                writer.Write((short)cz);

                writer.Write((uint)chunkOffsets[i]);
                writer.Write(chunkLengths[i]);
            }

            binaryData = ms.ToArray();
        }

        if (verbose)
        {
            File.WriteAllBytes(outputPath, binaryData);
            Console.WriteLine($"Binary file written to {outputPath}");
        }
        // Log how many objects were included in the binary
        Console.WriteLine($"Objects included in binary: {validObjects.Count}");

        // Compress the binary data with Deflate
        // disabled for now as we are running into other runtime limits before hitting file size limits
        // using (var compressedStream = new MemoryStream())
        // {
        //     using (var deflateStream = new DeflateStream(compressedStream, CompressionMode.Compress))
        //     {
        //         deflateStream.Write(binaryData, 0, binaryData.Length);
        //     }
        //     binaryData = compressedStream.ToArray();
        // }

        // Create JSON with encoded data split into 200-character segments
        string encoded = Utilities.EncodeCustomBase16(binaryData);
        var jsonChunks = new JsonObject();
        int chunkSize = 200;
        int keyIndex = 0;
        for (int i = 0; i < encoded.Length; i += chunkSize)
        {
            string chunk = encoded.Substring(i, Math.Min(chunkSize, encoded.Length - i));
            jsonChunks[$"A{keyIndex:X}"] = chunk;
            keyIndex++;
        }

        string jsonOutput = jsonChunks.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string stringsPath = inputPath.Replace(".spatial.json", ".strings.json");
        File.WriteAllText(stringsPath, jsonOutput);
        Console.WriteLine($"Encoded JSON written to {stringsPath}");

        var filteredPortalDynamic = portalDynamicElement.EnumerateArray().Where(e => !compressedObjects.Contains(e)).Select(e => JsonNode.Parse(e.GetRawText())).ToList();
        var filteredStatic = staticElement.EnumerateArray().Where(e => !compressedObjects.Contains(e)).Select(e => JsonNode.Parse(e.GetRawText())).ToList();

        var filteredDoc = new JsonObject
        {
            ["Portal_Dynamic"] = new JsonArray(filteredPortalDynamic.ToArray()),
            ["Static"] = new JsonArray(filteredStatic.ToArray())
        };

        string filteredJson = filteredDoc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string filteredPath = inputPath.Replace(".spatial.json", "_filtered.spatial.json");
        File.WriteAllText(filteredPath, filteredJson);
        Console.WriteLine($"Filtered JSON written to {filteredPath}");
    }

    /// <summary>
    /// Converts a rotation matrix (from right, up, front vectors) to Euler angles (Pitch, Yaw, Roll) in radians.
    /// This matches Godot's Basis.get_euler(EULER_ORDER_YXZ) implementation exactly.
    /// The vectors are stored as columns in Godot: Basis = [right | up | front]
    /// All angles are returned in radians.
    /// </summary>
    public static Vector MatrixToAnglesRadians(Vector right, Vector up, Vector front, Vector scale)
    {
        const double epsilon = 0.00000025;

        // Avoid division by zero if an object has zero scale
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0)
        {
            return new Vector { X = 0, Y = 0, Z = 0 };
        }

        // Create normalized vectors by dividing out the scale
        Vector normalizedRight = right / scale.X;
        Vector normalizedUp = up / scale.Y;
        Vector normalizedFront = front / scale.Z;

        // Godot stores basis as columns, but accesses via rows[i][j]
        // So: rows[0] = (right.x, up.x, front.x)
        //     rows[1] = (right.y, up.y, front.y)
        //     rows[2] = (right.z, up.z, front.z)
        double rows_0_0 = normalizedRight.X, rows_0_1 = normalizedUp.X, rows_0_2 = normalizedFront.X;
        double rows_1_0 = normalizedRight.Y, rows_1_1 = normalizedUp.Y, rows_1_2 = normalizedFront.Y;
        double rows_2_0 = normalizedRight.Z, rows_2_1 = normalizedUp.Z, rows_2_2 = normalizedFront.Z;

        double m12 = rows_1_2;
        double pitch, yaw, roll;

        if (m12 < (1 - epsilon))
        {
            if (m12 > -(1 - epsilon))
            {
                // Check for pure X rotation (simplified form)
                if (rows_1_0 == 0 && rows_0_1 == 0 && rows_0_2 == 0 &&
                    rows_2_0 == 0 && rows_0_0 == 1)
                {
                    pitch = Math.Atan2(-m12, rows_1_1);
                    yaw = 0;
                    roll = 0;
                }
                else
                {
                    pitch = Math.Asin(-m12);
                    yaw = Math.Atan2(rows_0_2, rows_2_2);
                    roll = Math.Atan2(rows_1_0, rows_1_1);
                }
            }
            else // m12 == -1 (gimbal lock)
            {
                pitch = Math.PI * 0.5;
                yaw = Math.Atan2(rows_0_1, rows_0_0);
                roll = 0;
            }
        }
        else // m12 == 1 (gimbal lock)
        {
            pitch = -Math.PI * 0.5;
            yaw = -Math.Atan2(rows_0_1, rows_0_0);
            roll = 0;
        }

        return new Vector
        {
            X = pitch * -1,   // Pitch in radians (rotation around X-axis)
            Y = yaw,     // Yaw in radians (rotation around Y-axis)
            Z = roll * -1 // Roll in radians (rotation around Z-axis) multiplied by -1 to match the portal runtime expectations
        };
    }

    public static double RadiansToDegrees(double radians)
    {
        return radians * (180.0 / Math.PI);
    }
}