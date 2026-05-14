// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RuntimeMigrator;

/// <summary>
/// High-level orchestrator for the spatial JSON-to-binary migration workflow.
/// </summary>
public static class SpatialMigrator
{
    /// <summary>
    /// Options controlling a spatial JSON-to-binary conversion.
    /// </summary>
    public sealed record Options
    {
        public required string InputPath { get; init; }
        public required string OutputPath { get; init; }
        public bool Verbose { get; init; }
    }

    /// <summary>
    /// Information about what was produced during conversion.
    /// </summary>
    public sealed record ConversionResult
    {
        public required byte[]? RawBinary { get; init; }
        public required string StringsPath { get; init; }
        public required string FilteredPath { get; init; }
        public required int TotalObjects { get; init; }
        public required int CompressedCount { get; init; }
        public required int ChunkCount { get; init; }
        public required int ScalePaletteCount { get; init; }
        public required int RotationPaletteCount { get; init; }
        public required int TypePaletteCount { get; init; }
        public required int DroppedScaleCount { get; init; }
        public required int DroppedRotationCount { get; init; }
        public required string MapType { get; init; }
    }

    /// <summary>
    /// Run the full JSON-to-binary conversion.
    /// </summary>
    public static ConversionResult Convert(Options options)
    {
        using FileStream fileStream = File.OpenRead(options.InputPath);
        using JsonDocument document = JsonDocument.Parse(fileStream);

        if (!document.RootElement.TryGetProperty("Portal_Dynamic", out var portalDynamicElement))
            throw new InvalidOperationException("Missing required section 'Portal_Dynamic' in spatial JSON.");
        if (!document.RootElement.TryGetProperty("Static", out var staticElement))
            throw new InvalidOperationException("Missing required section 'Static' in spatial JSON.");

        int totalObjects = portalDynamicElement.GetArrayLength() + staticElement.GetArrayLength();
        if (totalObjects == 0)
        {
            throw new InvalidOperationException("No objects found in JSON");
        }

        var classification = SpatialObjectClassifier.Classify(document);

        // Write binary data
        var (binaryData, chunkCount) = SpatialBinaryWriter.Write(classification);

        // Write raw binary if verbose
        if (options.Verbose)
        {
            File.WriteAllBytes(options.OutputPath, binaryData);
        }

        // Write strings.json
        string basePath = Path.Combine(
            Path.GetDirectoryName(options.OutputPath) ?? "",
            Path.GetFileNameWithoutExtension(options.OutputPath));

        string stringsPath = basePath + ".strings.json";
        StringsJsonWriter.Write(binaryData, stringsPath);

        // Write filtered spatial JSON
        string filteredPath = basePath + "_filtered.spatial.json";
        FilteredSpatialJsonWriter.Write(document, classification, filteredPath);

        return new ConversionResult
        {
            RawBinary = options.Verbose ? binaryData : null,
            StringsPath = stringsPath,
            FilteredPath = filteredPath,
            TotalObjects = totalObjects,
            CompressedCount = classification.CompressibleObjects.Count,
            ChunkCount = chunkCount,
            ScalePaletteCount = classification.ScalePalette.Count,
            RotationPaletteCount = classification.RotationPalette.Count,
            TypePaletteCount = classification.TypePalette.Count,
            DroppedScaleCount = classification.DroppedScaleCount,
            DroppedRotationCount = classification.DroppedRotationCount,
            MapType = classification.MapType
        };
    }
}
