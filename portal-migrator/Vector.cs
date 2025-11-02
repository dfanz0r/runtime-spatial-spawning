// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Globalization;

public class Vector
{
    [JsonPropertyName("x")]
    public float X { get; set; }
    [JsonPropertyName("y")]
    public float Y { get; set; }
    [JsonPropertyName("z")]
    public float Z { get; set; }

    public float Magnitude() => (float)Math.Sqrt(X * X + Y * Y + Z * Z);

    // Componentwise min and max
    public static Vector Min(Vector a, Vector b) => new Vector { X = Math.Min(a.X, b.X), Y = Math.Min(a.Y, b.Y), Z = Math.Min(a.Z, b.Z) };
    public static Vector Max(Vector a, Vector b) => new Vector { X = Math.Max(a.X, b.X), Y = Math.Max(a.Y, b.Y), Z = Math.Max(a.Z, b.Z) };

    // Binary serialization methods
    public void WriteTo(BinaryWriter writer)
    {
        writer.Write(X);
        writer.Write(Y);
        writer.Write(Z);
    }

    public static Vector ReadFrom(BinaryReader reader)
    {
        return new Vector
        {
            X = reader.ReadSingle(),
            Y = reader.ReadSingle(),
            Z = reader.ReadSingle()
        };
    }

    public static Vector ReadUShortVector(BinaryReader reader)
    {
        return new Vector
        {
            X = reader.ReadUInt16(),
            Y = reader.ReadUInt16(),
            Z = reader.ReadUInt16()
        };
    }

    public static Vector ReadShortVector(BinaryReader reader)
    {
        return new Vector
        {
            X = reader.ReadInt16(),
            Y = reader.ReadInt16(),
            Z = reader.ReadInt16()
        };
    }

    // Operators
    public static Vector operator +(Vector a, Vector b) => new Vector { X = a.X + b.X, Y = a.Y + b.Y, Z = a.Z + b.Z };
    public static Vector operator -(Vector a, Vector b) => new Vector { X = a.X - b.X, Y = a.Y - b.Y, Z = a.Z - b.Z };
    public static Vector operator *(Vector v, float s) => new Vector { X = v.X * s, Y = v.Y * s, Z = v.Z * s };
    public static Vector operator /(Vector v, float s) => new Vector { X = v.X / s, Y = v.Y / s, Z = v.Z / s };

    // Componentwise multiply
    public Vector Multiply(Vector other) => new Vector { X = X * other.X, Y = Y * other.Y, Z = Z * other.Z };

    // Quantization methods
    public (ushort, ushort, ushort) Quantize(float scale, float maxValue)
    {
        return (
            (ushort)Math.Clamp(X / scale * maxValue, 0, maxValue),
            (ushort)Math.Clamp(Y / scale * maxValue, 0, maxValue),
            (ushort)Math.Clamp(Z / scale * maxValue, 0, maxValue)
        );
    }

    public static Vector Dequantize(Vector quantized, float scale, float maxValue)
    {
        return new Vector
        {
            X = quantized.X / maxValue * scale,
            Y = quantized.Y / maxValue * scale,
            Z = quantized.Z / maxValue * scale
        };
    }

    public (ushort, ushort, ushort) QuantizeRotation(float range, float offset, float maxValue)
    {
        return (
            (ushort)Math.Clamp((X + offset) / range * maxValue, 0, maxValue),
            (ushort)Math.Clamp((Y + offset) / range * maxValue, 0, maxValue),
            (ushort)Math.Clamp((Z + offset) / range * maxValue, 0, maxValue)
        );
    }

    public static Vector DequantizeRotation(Vector quantized, float range, float offset, float maxValue)
    {
        return new Vector
        {
            X = (quantized.X / maxValue * range) - offset,
            Y = (quantized.Y / maxValue * range) - offset,
            Z = (quantized.Z / maxValue * range) - offset
        };
    }

    public Vector ToDegrees()
    {
        const float toDegrees = 180.0f / (float)Math.PI;
        return new Vector { X = X * toDegrees, Y = Y * toDegrees, Z = Z * toDegrees };
    }

    public override string ToString()
    {
        return $"new Vector({X.ToString(CultureInfo.InvariantCulture)}, {Y.ToString(CultureInfo.InvariantCulture)}, {Z.ToString(CultureInfo.InvariantCulture)})";
    }

    // Equality
    public override bool Equals(object? obj)
    {
        if (obj is Vector other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Z);
    }
}
