// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuntimeMigrator;

/// <summary>
/// Deserialized spatial object from JSON.
/// </summary>
public record SpatialObject
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;
    [JsonPropertyName("type")]
    public string Type { get; init; } = null!;
    [JsonPropertyName("position")]
    public Vector Position { get; init; }
    [JsonPropertyName("right")]
    public Vector Right { get; init; }
    [JsonPropertyName("up")]
    public Vector Up { get; init; }
    [JsonPropertyName("front")]
    public Vector Front { get; init; }
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>
/// CLI entry point for the RuntimeMigrator tool.
/// </summary>
public static class CodeGenerator
{
    public static int Main(string[] args)
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
            Console.Error.WriteLine("Usage: RuntimeMigrator <input.json> [output.bin] [--verbose]");
            return 1;
        }

        string inputPath = argList[0];

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: File not found at path '{Path.GetFullPath(inputPath)}'");
            return 2;
        }

        string outputPath = argList.Count > 1 ? argList[1] : "output.bin";
        var options = new SpatialMigrator.Options
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            Verbose = verbose
        };

        try
        {
            Console.WriteLine("Loading JSON");

            var result = SpatialMigrator.Convert(options);

            if (result.DroppedScaleCount > 0)
                Console.WriteLine($"Warning: {result.DroppedScaleCount} unique scale(s) exceeded byte palette limit and will be stored inline.");
            if (result.DroppedRotationCount > 0)
                Console.WriteLine($"Warning: {result.DroppedRotationCount} unique rotation(s) exceeded byte palette limit and will be stored inline.");

            Console.WriteLine($"Map Type: {result.MapType}");
            Console.WriteLine($"Total objects found: {result.TotalObjects}");
            Console.WriteLine($"Objects included in binary: {result.CompressedCount}");
            Console.WriteLine($"Encoded Object Count: {result.CompressedCount} Chunk Count: {result.ChunkCount}");
            Console.WriteLine($"Scale Palette Count: {result.ScalePaletteCount} Rotation Palette Count: {result.RotationPaletteCount} Type Palette Count: {result.TypePaletteCount}");

            if (verbose)
            {
                Console.WriteLine($"Binary file written to {outputPath}");
            }
            Console.WriteLine($"Encoded JSON written to {result.StringsPath}");
            Console.WriteLine($"Filtered JSON written to {result.FilteredPath}");
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 3;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 4;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error: Invalid JSON - {ex.Message}");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error: {ex.Message}");
            return 6;
        }

        return 0;
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
            return new Vector(0, 0, 0);
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
