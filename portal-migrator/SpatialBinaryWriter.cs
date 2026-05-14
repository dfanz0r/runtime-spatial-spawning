// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RuntimeMigrator;

/// <summary>
/// Writes the binary format for compressed spatial objects.
/// </summary>
public static class SpatialBinaryWriter
{
    private const float ChunkSize = 64.0f;
    private const float MaxUint16 = 65535.0f;
    private const float ScaleMax = 100.0f;
    private const float RotationRange = (float)Math.PI * 2;
    private const byte MaxPaletteIndex = 254;
    private const byte CustomPaletteSentinel = 255;

    /// <summary>
    /// Writes the binary representation of classified spatial data to a byte array.
    /// </summary>
    /// <returns>(binary data, number of chunks written)</returns>
    public static (byte[] Data, int ChunkCount) Write(SpatialObjectClassifier.ClassificationResult data)
    {
        // Group into chunks
        var chunks = new Dictionary<(int x, int y, int z), List<(Vector position, Vector scale, Vector rotation, int typeIndex)>>();

        foreach (var item in data.CompressibleObjects)
        {
            int cx = (int)Math.Floor(item.Original.Position.X / ChunkSize);
            int cy = (int)Math.Floor(item.Original.Position.Y / ChunkSize);
            int cz = (int)Math.Floor(item.Original.Position.Z / ChunkSize);
            var key = (cx, cy, cz);

            if (!chunks.ContainsKey(key))
                chunks[key] = [];

            int typeIndexValue = data.TypeNameToIndex[item.TypeName];
            chunks[key].Add((item.Original.Position, item.Scale, item.Rotation, typeIndexValue));
        }

        // Validate narrowing limits before writing
        if (chunks.Count > ushort.MaxValue)
            throw new InvalidOperationException($"Chunk count {chunks.Count} exceeds ushort maximum ({ushort.MaxValue}).");

        if (data.TypePalette.Count > ushort.MaxValue)
            throw new InvalidOperationException($"Type palette count {data.TypePalette.Count} exceeds ushort maximum ({ushort.MaxValue}).");

        foreach (var key in chunks.Keys)
        {
            var (cx, cy, cz) = key;
            if (cx < short.MinValue || cx > short.MaxValue ||
                cy < short.MinValue || cy > short.MaxValue ||
                cz < short.MinValue || cz > short.MaxValue)
                throw new InvalidOperationException($"Chunk coordinate ({cx}, {cy}, {cz}) exceeds short range.");

            if (chunks[key].Count > ushort.MaxValue)
                throw new InvalidOperationException($"Object count {chunks[key].Count} in chunk ({cx},{cy},{cz}) exceeds ushort maximum.");
        }

        if (data.TypePalette.Count > ushort.MaxValue)
            throw new InvalidOperationException($"Type palette count {data.TypePalette.Count} exceeds ushort maximum ({ushort.MaxValue}).");

        byte[] binaryData;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            // Header
            writer.Write((ushort)1); // format version
            writer.Write(ChunkSize);
            writer.Write(data.MapType);

            writer.Write(data.CompressibleObjects.Count);
            writer.Write((ushort)chunks.Count);

            // Object bounds min/max
            data.MinBounds.WriteToAsFloat(writer);
            data.MaxBounds.WriteToAsFloat(writer);

            writer.Write((ushort)data.ScalePalette.Count);
            writer.Write((ushort)data.RotationPalette.Count);
            writer.Write((ushort)data.TypePalette.Count);

            // Scale palette
            foreach (var s in data.ScalePalette)
                s.WriteToAsFloat(writer);

            // Rotation palette
            foreach (var r in data.RotationPalette)
                r.WriteToAsFloat(writer);

            // Type palette
            foreach (var t in data.TypePalette)
                writer.Write(t);

            // Chunk index placeholder
            long chunkIndexPos = ms.Position;
            // Sort chunk keys deterministically by (x, y, z) so repeated runs produce identical output
            var chunkKeys = chunks.Keys.OrderBy(k => k.x).ThenBy(k => k.y).ThenBy(k => k.z).ToList();
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

                Vector origin = new(cx * ChunkSize, cy * ChunkSize, cz * ChunkSize);

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

                    var (px, py, pz) = localPos.Quantize(ChunkSize, MaxUint16);
                    writer.Write(px);
                    writer.Write(py);
                    writer.Write(pz);

                    bool hasScaleIdx = data.ScaleIndex.TryGetValue(scale, out int scaleIdx) && scaleIdx <= MaxPaletteIndex;
                    byte scaleIdxByte = hasScaleIdx ? (byte)scaleIdx : CustomPaletteSentinel;
                    writer.Write(scaleIdxByte);

                    bool hasRotIdx = data.RotationIndex.TryGetValue(rotation, out int rotIdx) && rotIdx <= MaxPaletteIndex;
                    byte rotIdxByte = hasRotIdx ? (byte)rotIdx : CustomPaletteSentinel;
                    writer.Write(rotIdxByte);

                    writer.Write((ushort)typeId);

                    if (!hasScaleIdx)
                    {
                        var (sx, sy, sz) = scale.Quantize(ScaleMax, MaxUint16);
                        writer.Write(sx);
                        writer.Write(sy);
                        writer.Write(sz);
                    }

                    if (!hasRotIdx)
                    {
                        var (rx, ry, rz) = rotation.QuantizeRotation(RotationRange, Math.PI, MaxUint16);
                        writer.Write(rx);
                        writer.Write(ry);
                        writer.Write(rz);
                    }
                }

                long endPos = ms.Position;
                chunkLengths.Add((uint)(endPos - startPos));
            }

            // Write chunk index
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

        return (binaryData, chunks.Count);
    }
}
