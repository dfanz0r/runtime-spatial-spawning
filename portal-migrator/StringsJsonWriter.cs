// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuntimeMigrator;

/// <summary>
/// Writes the encoded binary data as a chunked custom-base16 strings JSON file.
/// </summary>
public static class StringsJsonWriter
{
    private const int DefaultChunkSize = 200;

    /// <summary>
    /// Writes the .strings.json file with the binary data encoded in custom base16 chunks.
    /// </summary>
    public static void Write(byte[] binaryData, string outputPath, int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(binaryData);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "Chunk size must be positive.");

        string encoded = Utilities.EncodeCustomBase16(binaryData);
        var jsonChunks = new JsonObject();
        int keyIndex = 0;
        for (int i = 0; i < encoded.Length; i += chunkSize)
        {
            string chunk = encoded.Substring(i, Math.Min(chunkSize, encoded.Length - i));
            jsonChunks[$"A{keyIndex:X}"] = chunk;
            keyIndex++;
        }

        string jsonOutput = jsonChunks.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(outputPath, jsonOutput);
    }
}
