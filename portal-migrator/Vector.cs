// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Globalization;

namespace RuntimeMigrator;

public readonly record struct Vector(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z)
{
    // Uses default record struct equality (exact double comparison).
    // Vectors are rounded before being used as dictionary keys in palette construction,
    // so exact comparison is the correct contract for those lookup tables.

    public double Magnitude() => Math.Sqrt(X * X + Y * Y + Z * Z);

    public static Vector Min(Vector a, Vector b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vector Max(Vector a, Vector b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public void WriteToAsFloat(BinaryWriter writer)
    {
        writer.Write((float)X);
        writer.Write((float)Y);
        writer.Write((float)Z);
    }

    public static Vector ReadFromAsFloat(BinaryReader reader)
    {
        return new Vector(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
    }

    public static Vector ReadUShortVector(BinaryReader reader)
    {
        return new Vector(
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16());
    }

    public static Vector ReadShortVector(BinaryReader reader)
    {
        return new Vector(
            reader.ReadInt16(),
            reader.ReadInt16(),
            reader.ReadInt16());
    }

    public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector operator *(Vector v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector operator /(Vector v, double s) => new(v.X / s, v.Y / s, v.Z / s);

    public Vector Multiply(Vector other) => new(X * other.X, Y * other.Y, Z * other.Z);

    public (ushort, ushort, ushort) Quantize(double scale, double maxValue)
    {
        return (
            (ushort)Math.Clamp(Math.Round(X / scale * maxValue), 0, maxValue),
            (ushort)Math.Clamp(Math.Round(Y / scale * maxValue), 0, maxValue),
            (ushort)Math.Clamp(Math.Round(Z / scale * maxValue), 0, maxValue)
        );
    }

    public static Vector Dequantize(Vector quantized, double scale, double maxValue)
    {
        return new Vector(
            quantized.X / maxValue * scale,
            quantized.Y / maxValue * scale,
            quantized.Z / maxValue * scale);
    }

    public Vector Dequantize(double scale, double maxValue) => Dequantize(this, scale, maxValue);

    public static Vector Round(Vector v, int decimals)
    {
        return new Vector(
            Math.Round(v.X, decimals),
            Math.Round(v.Y, decimals),
            Math.Round(v.Z, decimals));
    }

    public (ushort, ushort, ushort) QuantizeRotation(double range, double offset, double maxValue)
    {
        return (
            (ushort)Math.Clamp(Math.Round((X + offset) / range * maxValue), 0, maxValue),
            (ushort)Math.Clamp(Math.Round((Y + offset) / range * maxValue), 0, maxValue),
            (ushort)Math.Clamp(Math.Round((Z + offset) / range * maxValue), 0, maxValue)
        );
    }

    public static Vector DequantizeRotation(Vector quantized, double range, double offset, double maxValue)
    {
        return new Vector(
            (quantized.X / maxValue * range) - offset,
            (quantized.Y / maxValue * range) - offset,
            (quantized.Z / maxValue * range) - offset);
    }

    public Vector DequantizeRotation(double range, double offset, double maxValue) => DequantizeRotation(this, range, offset, maxValue);

    public Vector ToDegrees()
    {
        const double toDegrees = 180.0 / Math.PI;
        return new Vector(X * toDegrees, Y * toDegrees, Z * toDegrees);
    }

    public override string ToString()
    {
        return $"new Vector({X.ToString(CultureInfo.InvariantCulture)}, {Y.ToString(CultureInfo.InvariantCulture)}, {Z.ToString(CultureInfo.InvariantCulture)})";
    }
}
