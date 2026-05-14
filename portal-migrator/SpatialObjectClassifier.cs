// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RuntimeMigrator;

/// <summary>
/// Classifies spatial JSON objects as compressible (eligible for binary encoding)
/// or retained (must stay in filtered JSON). Tracks referenced ids from incompressible
/// objects so referenced targets are also retained.
/// </summary>
public static class SpatialObjectClassifier
{
    private const byte MaxPaletteIndex = 254;

    private static readonly HashSet<string> _baseKeys = new() { "name", "type", "position", "right", "up", "front", "id" };
    private static readonly HashSet<string> _skipNames = new() { "metadata/_edit_group_", "metadata/organizer_group", "metadata/organizer_symbol", "metadata/organizer_color" };

    /// <summary>
    /// Result of classification.
    /// </summary>
    public sealed record ClassificationResult
    {
        public required List<CompressibleObject> CompressibleObjects { get; init; }
        public required HashSet<string> CompressibleIds { get; init; }
        public required List<Vector> ScalePalette { get; init; }
        public required List<Vector> RotationPalette { get; init; }
        public required List<string> TypePalette { get; init; }
        public required Dictionary<Vector, int> ScaleIndex { get; init; }
        public required Dictionary<Vector, int> RotationIndex { get; init; }
        public required Dictionary<string, int> TypeNameToIndex { get; init; }
        public required string MapType { get; init; }
        public required Vector MinBounds { get; init; }
        public required Vector MaxBounds { get; init; }
        public required int DroppedScaleCount { get; init; }
        public required int DroppedRotationCount { get; init; }
    }

    /// <summary>
    /// A single compressible object with pre-computed values.
    /// </summary>
    public sealed record CompressibleObject
    {
        public required SpatialObject Original { get; init; }
        public required Vector Scale { get; init; }
        public required Vector Rotation { get; init; }
        public required string TypeName { get; init; }
    }

    /// <summary>
    /// Classify objects in a spatial JSON document.
    /// </summary>
    public static ClassificationResult Classify(JsonDocument document)
    {
        var portalDynamicElement = document.RootElement.GetProperty("Portal_Dynamic");
        var staticElement = document.RootElement.GetProperty("Static");

        // Determine map type from the first static object's type field
        string mapType = DetermineMapType(staticElement);

        var skipIds = new HashSet<string>
        {
            $"Static/MP_{mapType}_Terrain",
            $"Static/MP_{mapType}_Assets"
        };

        var compressibleIds = new HashSet<string>();
        var referencedIds = new HashSet<string>();

        // First pass: identify incompressible objects and collect referenced ids
        ProcessElements(portalDynamicElement, skipIds, compressibleIds, referencedIds);
        ProcessElements(staticElement, skipIds, compressibleIds, referencedIds);

        // Remove referenced ids from compressible set
        foreach (var refId in referencedIds)
            compressibleIds.Remove(refId);

        // Second pass: build compressible object list
        var validObjects = new List<CompressibleObject>();
        var minBounds = new Vector(float.MaxValue, float.MaxValue, float.MaxValue);
        var maxBounds = new Vector(float.MinValue, float.MinValue, float.MinValue);

        CollectCompressible(portalDynamicElement, compressibleIds, skipIds, validObjects, ref minBounds, ref maxBounds);
        CollectCompressible(staticElement, compressibleIds, skipIds, validObjects, ref minBounds, ref maxBounds);

        // Default to zero bounds when no objects were compressed
        if (validObjects.Count == 0)
        {
            minBounds = new Vector(0, 0, 0);
            maxBounds = new Vector(0, 0, 0);
        }

        // Build palettes
        var scalePalette = new List<Vector>();
        var scaleIndex = new Dictionary<Vector, int>();
        var rotationPalette = new List<Vector>();
        var rotationIndex = new Dictionary<Vector, int>();
        var typePalette = new List<string>();
        var typeNameToIndex = new Dictionary<string, int>();

        int droppedScales = 0, droppedRotations = 0;
        foreach (var obj in validObjects)
        {
            if (!scaleIndex.ContainsKey(obj.Scale))
            {
                if (scalePalette.Count <= MaxPaletteIndex)
                {
                    scaleIndex[obj.Scale] = scalePalette.Count;
                    scalePalette.Add(obj.Scale);
                }
                else
                {
                    droppedScales++;
                }
            }
            if (!rotationIndex.ContainsKey(obj.Rotation))
            {
                if (rotationPalette.Count <= MaxPaletteIndex)
                {
                    rotationIndex[obj.Rotation] = rotationPalette.Count;
                    rotationPalette.Add(obj.Rotation);
                }
                else
                {
                    droppedRotations++;
                }
            }
            if (!typeNameToIndex.ContainsKey(obj.TypeName))
            {
                typeNameToIndex[obj.TypeName] = typePalette.Count;
                typePalette.Add(obj.TypeName);
            }
        }

        return new ClassificationResult
        {
            CompressibleObjects = validObjects,
            CompressibleIds = compressibleIds,
            ScalePalette = scalePalette,
            RotationPalette = rotationPalette,
            TypePalette = typePalette,
            ScaleIndex = scaleIndex,
            RotationIndex = rotationIndex,
            TypeNameToIndex = typeNameToIndex,
            MapType = mapType,
            MinBounds = minBounds,
            MaxBounds = maxBounds,
            DroppedScaleCount = droppedScales,
            DroppedRotationCount = droppedRotations
        };
    }

    /// <summary>
    /// Returns true if an object at the given JsonElement should be retained in filtered output
    /// (i.e., it is NOT compressible).
    /// </summary>
    public static bool IsRetained(JsonElement objElement, HashSet<string> compressibleIds, string mapType)
    {
        var skipIds = new HashSet<string>
        {
            $"Static/MP_{mapType}_Terrain",
            $"Static/MP_{mapType}_Assets"
        };

        string? id = objElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

        if (id != null && (skipIds.Contains(id) || id.StartsWith("[STATIC]")))
            return true; // explicitly skipped -> retained

        if (!string.IsNullOrEmpty(id))
            return !compressibleIds.Contains(id);

        return HasExtraData(objElement);
    }

    private static bool HasExtraData(JsonElement objElement)
    {
        foreach (var prop in objElement.EnumerateObject())
        {
            if (!_baseKeys.Contains(prop.Name) && !_skipNames.Contains(prop.Name))
                return true;
        }
        return false;
    }

    private static string DetermineMapType(JsonElement staticElement)
    {
        if (staticElement.GetArrayLength() == 0)
            return "Unknown";

        var firstStatic = staticElement[0];
        if (firstStatic.TryGetProperty("type", out var typeProp))
        {
            string type = typeProp.GetString() ?? "";
            var parts = type.Split('_');
            if (parts.Length >= 3 && parts[0] == "MP")
            {
                return parts[1];
            }
        }
        return "Unknown";
    }

    private static void ProcessElements(
        JsonElement array,
        HashSet<string> skipIds,
        HashSet<string> compressibleIds,
        HashSet<string> referencedIds)
    {
        foreach (var objElement in array.EnumerateArray())
        {
            string? id = objElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            if (id != null && (skipIds.Contains(id) || id.StartsWith("[STATIC]")))
                continue;

            if (HasExtraData(objElement))
            {
                CollectReferencedIds(objElement, referencedIds);
            }
            else if (!string.IsNullOrEmpty(id))
            {
                compressibleIds.Add(id);
            }
        }
    }

    private static void CollectReferencedIds(JsonElement objElement, HashSet<string> referencedIds)
    {
        if (!objElement.TryGetProperty("linked", out var linkedProp) || linkedProp.ValueKind != JsonValueKind.Array)
            return;

        foreach (var linkedItem in linkedProp.EnumerateArray())
        {
            if (linkedItem.ValueKind != JsonValueKind.String)
                continue;

            string? linkedKey = linkedItem.GetString();
            if (string.IsNullOrEmpty(linkedKey))
                continue;

            if (objElement.TryGetProperty(linkedKey, out var refProp))
            {
                if (refProp.ValueKind == JsonValueKind.String)
                {
                    string? refId = refProp.GetString();
                    if (!string.IsNullOrEmpty(refId))
                        referencedIds.Add(refId);
                }
                else if (refProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var refItem in refProp.EnumerateArray())
                    {
                        if (refItem.ValueKind == JsonValueKind.String)
                        {
                            string? refId = refItem.GetString();
                            if (!string.IsNullOrEmpty(refId))
                                referencedIds.Add(refId);
                        }
                    }
                }
            }
        }
    }

    private static void CollectCompressible(
        JsonElement array,
        HashSet<string> compressibleIds,
        HashSet<string> skipIds,
        List<CompressibleObject> validObjects,
        ref Vector minBounds,
        ref Vector maxBounds)
    {
        foreach (var objElement in array.EnumerateArray())
        {
            string? id = objElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            if (id != null && (skipIds.Contains(id) || id.StartsWith("[STATIC]")))
                continue;

            if (!string.IsNullOrEmpty(id))
            {
                if (!compressibleIds.Contains(id))
                    continue;
            }
            else
            {
                if (HasExtraData(objElement))
                    continue;
            }

            var obj = objElement.Deserialize<SpatialObject>();
            if (obj == null || obj.Type == null)
                continue;

            Vector position = obj.Position;
            var scale = new Vector(
                obj.Right.Magnitude(),
                obj.Up.Magnitude(),
                obj.Front.Magnitude()
            );
            Vector rotation = CodeGenerator.MatrixToAnglesRadians(obj.Right, obj.Up, obj.Front, scale);

            validObjects.Add(new CompressibleObject
            {
                Original = obj,
                Scale = Vector.Round(scale, 2),
                Rotation = rotation,
                TypeName = obj.Type
            });

            minBounds = Vector.Min(minBounds, position);
            maxBounds = Vector.Max(maxBounds, position);
        }
    }
}
