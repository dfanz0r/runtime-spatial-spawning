// Copyright (c) 2025 Matt Sitton (dfanz0r)
// Licensed under the BSD 2-Clause License.
// See the LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.Text;

public static class Utilities
{
    private const string CUSTOM_BASE16_TABLE = "_-,.:`~'+=^%<}] ";
    private static readonly Dictionary<char, byte> CUSTOM_BASE16_MAP;

    static Utilities()
    {
        CUSTOM_BASE16_MAP = new Dictionary<char, byte>(32);
        for (byte i = 0; i < 16; i++)
        {
            char upper = CUSTOM_BASE16_TABLE[i];
            char lower = char.ToLowerInvariant(upper);
            CUSTOM_BASE16_MAP[upper] = i;
            CUSTOM_BASE16_MAP[lower] = i; // allow lowercase too
        }
    }

    /// <summary>
    /// Encodes bytes into Base16 (hexadecimal) string.
    /// </summary>
    public static string EncodeCustomBase16(byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (data.Length == 0)
            return string.Empty;

        char[] chars = new char[data.Length * 2];
        int c = 0;
        foreach (byte b in data)
        {
            chars[c++] = CUSTOM_BASE16_TABLE[b >> 4];
            chars[c++] = CUSTOM_BASE16_TABLE[b & 0xF];
        }

        return new string(chars);
    }

    /// <summary>
    /// Decodes a Base16 (hexadecimal) string back into bytes.
    /// Accepts both upper and lower case.
    /// </summary>
    public static byte[] DecodeCustomBase16(string data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (data.Length == 0)
            return Array.Empty<byte>();

        if ((data.Length & 1) != 0)
            throw new FormatException("Hex string length must be even.");

        byte[] result = new byte[data.Length / 2];

        for (int i = 0, j = 0; i < data.Length; i += 2, j++)
        {
            if (!CUSTOM_BASE16_MAP.TryGetValue(data[i], out byte hi) ||
                !CUSTOM_BASE16_MAP.TryGetValue(data[i + 1], out byte lo))
                throw new FormatException($"Invalid hex character at index {i}.");

            result[j] = (byte)((hi << 4) | lo);
        }

        return result;
    }
}
