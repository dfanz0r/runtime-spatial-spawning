// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;

namespace RuntimeMigrator;

public static class Utilities
{
    // 16 symbols for custom hex encoding.
    private const string CUSTOM_BASE16_TABLE = "_-,.:`~'+=^%<}] ";

    // 256-byte lookup: index by char code, value = nibble (0-15) or 0xFF for invalid.
    private static readonly byte[] CUSTOM_BASE16_LOOKUP = BuildLookupTable();

    private static byte[] BuildLookupTable()
    {
        byte[] table = new byte[256];
        Array.Fill(table, (byte)0xFF);

        for (byte i = 0; i < 16; i++)
        {
            char upper = CUSTOM_BASE16_TABLE[i];
            table[(byte)upper] = i;

            char lower = char.ToLowerInvariant(upper);
            if (lower != upper)
                table[(byte)lower] = i;
        }

        return table;
    }

    /// <summary>
    /// Encodes bytes into a custom Base16 string.
    /// </summary>
    public static string EncodeCustomBase16(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

        return string.Create(data.Length * 2, data, (chars, bytes) =>
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = CUSTOM_BASE16_TABLE[b >> 4];
                chars[i * 2 + 1] = CUSTOM_BASE16_TABLE[b & 0xF];
            }
        });
    }

    /// <summary>
    /// Decodes a custom Base16 string back into bytes.
    /// Accepts both upper and lower case.
    /// </summary>
    public static byte[] DecodeCustomBase16(string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
            return Array.Empty<byte>();

        if ((data.Length & 1) != 0)
            throw new FormatException("Hex string length must be even.");

        byte[] result = new byte[data.Length / 2];
        ReadOnlySpan<byte> lookup = CUSTOM_BASE16_LOOKUP;

        for (int i = 0, j = 0; i < data.Length; i += 2, j++)
        {
            int hiCode = data[i];
            int loCode = data[i + 1];

            if (hiCode >= lookup.Length || loCode >= lookup.Length)
                throw new FormatException($"Invalid hex character at index {i}.");

            byte hi = lookup[hiCode];
            byte lo = lookup[loCode];

            if (hi == 0xFF || lo == 0xFF)
                throw new FormatException($"Invalid hex character at index {i}.");

            result[j] = (byte)((hi << 4) | lo);
        }

        return result;
    }
}
