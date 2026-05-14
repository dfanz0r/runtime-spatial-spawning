// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System.IO;
using System.Text.Json;

namespace RuntimeMigrator;

/// <summary>
/// Writes the filtered spatial JSON containing only retained (non-compressible) objects.
/// </summary>
public static class FilteredSpatialJsonWriter
{
    /// <summary>
    /// Writes the _filtered.spatial.json file with objects that are not compressible.
    /// </summary>
    public static void Write(
        JsonDocument sourceDocument,
        SpatialObjectClassifier.ClassificationResult result,
        string outputPath)
    {
        var portalDynamicElement = sourceDocument.RootElement.GetProperty("Portal_Dynamic");
        var staticElement = sourceDocument.RootElement.GetProperty("Static");

        using FileStream fs = File.Create(outputPath);
        using Utf8JsonWriter writer = new(fs, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        writer.WritePropertyName("Portal_Dynamic");
        writer.WriteStartArray();
        foreach (var obj in portalDynamicElement.EnumerateArray())
        {
            if (SpatialObjectClassifier.IsRetained(obj, result.CompressibleIds, result.MapType))
                obj.WriteTo(writer);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("Static");
        writer.WriteStartArray();
        foreach (var obj in staticElement.EnumerateArray())
        {
            if (SpatialObjectClassifier.IsRetained(obj, result.CompressibleIds, result.MapType))
                obj.WriteTo(writer);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }
}
