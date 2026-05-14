using System;
using System.Collections.Generic;
using System.IO;

namespace RuntimeMigrator.Tests;

/// <summary>
/// Test-side helper for reading the custom binary format produced by SpatialBinaryWriter.
/// Provides named properties instead of inline field parsing.
/// </summary>
internal sealed class BinaryFormatReader : IDisposable
{
    private readonly BinaryReader _reader;

    public BinaryFormatReader(byte[] data)
    {
        _reader = new BinaryReader(new MemoryStream(data));
        ReadHeader();
    }

    // ── Header ──────────────────────────────────────────────────────────────

    public ushort Version { get; private set; }
    public float ChunkSize { get; private set; }
    public string MapType { get; private set; } = "";
    public int TotalObjects { get; private set; }
    public ushort ChunkCount { get; private set; }

    // ── World Bounds ────────────────────────────────────────────────────────

    public Vector MinBounds { get; private set; }
    public Vector MaxBounds { get; private set; }

    // ── Palettes ────────────────────────────────────────────────────────────

    public ushort ScalePaletteCount { get; private set; }
    public ushort RotationPaletteCount { get; private set; }
    public ushort TypePaletteCount { get; private set; }

    public IReadOnlyList<Vector> ScalePalette { get; private set; } = Array.Empty<Vector>();
    public IReadOnlyList<Vector> RotationPalette { get; private set; } = Array.Empty<Vector>();
    public IReadOnlyList<string> TypePalette { get; private set; } = Array.Empty<string>();

    // ── Chunk Index ─────────────────────────────────────────────────────────

    public IReadOnlyList<ChunkEntry> Chunks { get; private set; } = Array.Empty<ChunkEntry>();

    public sealed record ChunkEntry(short X, short Y, short Z, uint Offset, uint Length);

    // ── Chunk Objects ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads all objects from the chunk at the given entry.
    /// </summary>
    public List<ChunkObject> ReadChunkObjects(ChunkEntry chunk)
    {
        _reader.BaseStream.Seek(chunk.Offset, SeekOrigin.Begin);
        ushort count = _reader.ReadUInt16();
        var objects = new List<ChunkObject>(count);

        for (int i = 0; i < count; i++)
        {
            ushort px = _reader.ReadUInt16();
            ushort py = _reader.ReadUInt16();
            ushort pz = _reader.ReadUInt16();

            byte scaleIndex = _reader.ReadByte();
            byte rotationIndex = _reader.ReadByte();
            ushort typeId = _reader.ReadUInt16();

            Vector? customScale = null;
            if (scaleIndex == 255)
            {
                customScale = Vector.ReadUShortVector(_reader);
            }

            Vector? customRotation = null;
            if (rotationIndex == 255)
            {
                customRotation = Vector.ReadUShortVector(_reader);
            }

            objects.Add(new ChunkObject
            {
                LocalPos = new Vector(px, py, pz),
                ScaleIndex = scaleIndex,
                RotationIndex = rotationIndex,
                TypeId = typeId,
                CustomScale = customScale,
                CustomRotation = customRotation
            });
        }

        return objects;
    }

    public sealed record ChunkObject
    {
        public required Vector LocalPos { get; init; }
        public required byte ScaleIndex { get; init; }
        public required byte RotationIndex { get; init; }
        public required ushort TypeId { get; init; }
        public Vector? CustomScale { get; init; }
        public Vector? CustomRotation { get; init; }
    }

    // ── Seek ────────────────────────────────────────────────────────────────

    /// <summary>Jump to an arbitrary offset (e.g. to re-read from start).</summary>
    public void Seek(long offset) => _reader.BaseStream.Seek(offset, SeekOrigin.Begin);

    public void Dispose() => _reader.Dispose();

    // ── Private parsing ─────────────────────────────────────────────────────

    private void ReadHeader()
    {
        Version = _reader.ReadUInt16();
        ChunkSize = _reader.ReadSingle();
        MapType = _reader.ReadString();
        TotalObjects = _reader.ReadInt32();
        ChunkCount = _reader.ReadUInt16();

        MinBounds = Vector.ReadFromAsFloat(_reader);
        MaxBounds = Vector.ReadFromAsFloat(_reader);

        ScalePaletteCount = _reader.ReadUInt16();
        RotationPaletteCount = _reader.ReadUInt16();
        TypePaletteCount = _reader.ReadUInt16();

        var scales = new Vector[ScalePaletteCount];
        for (int i = 0; i < ScalePaletteCount; i++)
            scales[i] = Vector.ReadFromAsFloat(_reader);
        ScalePalette = scales;

        var rotations = new Vector[RotationPaletteCount];
        for (int i = 0; i < RotationPaletteCount; i++)
            rotations[i] = Vector.ReadFromAsFloat(_reader);
        RotationPalette = rotations;

        var types = new string[TypePaletteCount];
        for (int i = 0; i < TypePaletteCount; i++)
            types[i] = _reader.ReadString();
        TypePalette = types;

        var chunks = new ChunkEntry[ChunkCount];
        for (int i = 0; i < ChunkCount; i++)
        {
            chunks[i] = new ChunkEntry(
                _reader.ReadInt16(),
                _reader.ReadInt16(),
                _reader.ReadInt16(),
                _reader.ReadUInt32(),
                _reader.ReadUInt32());
        }
        Chunks = chunks;
    }
}
