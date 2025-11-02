// using System;
// using System.Collections.Generic;
// using System.Linq; // Added for .SequenceEqual in testing, though not required for the class itself
// using System.Text;

// public static class Utilities
// {
//     private const string CUSTOM_BASE_TABLE = "_-,. `~'+=^%{}]:[?><&*;)$"; // Base is 25

//     // --- These parameters are calculated once based on the table above ---

//     private static readonly int BASE;
//     private static readonly int BYTES_PER_CHUNK;
//     private static readonly int CHARS_PER_CHUNK;
//     private static readonly Dictionary<char, int> CUSTOM_BASE_MAP;

//     /// <summary>
//     /// Static constructor to pre-calculate constants for the chosen base.
//     /// This runs only once when the class is first accessed.
//     /// </summary>
//     static Utilities()
//     {
//         BASE = CUSTOM_BASE_TABLE.Length;
//         if (BASE < 2)
//             throw new InvalidOperationException($"Table must have at least 2 characters, has {BASE}");

//         // Build a fast lookup map for decoding
//         CUSTOM_BASE_MAP = new Dictionary<char, int>();
//         for (int i = 0; i < CUSTOM_BASE_TABLE.Length; i++)
//         {
//             CUSTOM_BASE_MAP[CUSTOM_BASE_TABLE[i]] = i;
//         }

//         // We will process data in chunks that fit within a 64-bit ulong.
//         // 7 bytes (56 bits) is a safe and efficient choice.
//         BYTES_PER_CHUNK = 7;

//         // Calculate how many characters are needed to represent a full chunk of bytes.
//         // Formula: Chars = ceil(Bytes * log(256) / log(Base))
//         CHARS_PER_CHUNK = (int)Math.Ceiling(BYTES_PER_CHUNK * Math.Log(256) / Math.Log(BASE));
//     }

//     /// <summary>
//     /// Encodes a byte array into a string using a fast, chunk-based custom base conversion.
//     /// </summary>
//     public static string EncodeCustomBase(byte[] data)
//     {
//         if (data == null)
//             throw new ArgumentNullException(nameof(data));
//         if (data.Length == 0)
//             return string.Empty;

//         // Pre-allocate for efficiency
//         var result = new StringBuilder(data.Length * CHARS_PER_CHUNK / BYTES_PER_CHUNK + CHARS_PER_CHUNK);
//         var chunk = new char[CHARS_PER_CHUNK];

//         for (int i = 0; i < data.Length; i += BYTES_PER_CHUNK)
//         {
//             int bytesInThisChunk = Math.Min(BYTES_PER_CHUNK, data.Length - i);

//             // 1. Pack up to 7 bytes into a single ulong (fast)
//             ulong value = 0;
//             for (int j = 0; j < bytesInThisChunk; j++)
//             {
//                 value = (value << 8) | data[i + j];
//             }

//             // 2. Convert the ulong value to the custom base (fast)
//             for (int j = CHARS_PER_CHUNK - 1; j >= 0; j--)
//             {
//                 // *** FIX IS HERE ***
//                 // Math.DivRem does not support ulong. Use standard operators instead.
//                 ulong remainder = value % (ulong)BASE;
//                 value = value / (ulong)BASE;
//                 chunk[j] = CUSTOM_BASE_TABLE[(int)remainder];
//             }

//             result.Append(chunk);
//         }

//         return result.ToString();
//     }

//     /// <summary>
//     /// Decodes a string back to a byte array using the chunk-based custom base conversion.
//     /// </summary>
//     public static byte[] DecodeCustomBase(string data)
//     {
//         if (data == null)
//             throw new ArgumentNullException(nameof(data));
//         if (data.Length == 0)
//             return new byte[0];

//         int numChunks = (data.Length + CHARS_PER_CHUNK - 1) / CHARS_PER_CHUNK;
//         var buffer = new byte[numChunks * BYTES_PER_CHUNK];
//         int bufferPos = 0;

//         for (int i = 0; i < data.Length; i += CHARS_PER_CHUNK)
//         {
//             int charsInThisChunk = Math.Min(CHARS_PER_CHUNK, data.Length - i);

//             // 1. Convert the character chunk back into a ulong (fast)
//             ulong value = 0;
//             for (int j = 0; j < charsInThisChunk; j++)
//             {
//                 if (CUSTOM_BASE_MAP.TryGetValue(data[i + j], out int digit))
//                 {
//                     value = value * (ulong)BASE + (ulong)digit;
//                 }
//                 // NOTE: Invalid characters are currently skipped. You could throw an exception instead.
//             }

//             // 2. Unpack the ulong back into bytes (fast)
//             for (int j = BYTES_PER_CHUNK - 1; j >= 0; j--)
//             {
//                 // Shift and mask to extract each byte
//                 buffer[bufferPos + j] = (byte)(value & 0xFF);
//                 value >>= 8;
//             }
//             bufferPos += BYTES_PER_CHUNK;
//         }

//         // 3. Trim padding. The last chunk adds extra leading zero bytes if the
//         // original data was not a multiple of BYTES_PER_CHUNK.
//         // We calculate the expected original length and return only that part of the buffer.
//         long expectedLength = (long)Math.Floor(data.Length * Math.Log(BASE) / Math.Log(256));

//         if (buffer.Length <= expectedLength)
//         {
//             return buffer.Take((int)expectedLength).ToArray();
//         }

//         // The padding will be at the start of the buffer.
//         int padding = buffer.Length - (int)expectedLength;
//         byte[] finalResult = new byte[expectedLength];
//         Array.Copy(buffer, padding, finalResult, 0, expectedLength);

//         return finalResult;
//     }
// }
// using System;
// using System.Collections.Generic;
// using System.Text;

// public static class Utilities
// {
//     private const string CUSTOM_BASE_TABLE =
//         "_-,. `~'+=^%{}]:[?><&*;)$"; // Base is 25

//     private static readonly Dictionary<char, int> CUSTOM_BASE_MAP;
//     private static readonly int BASE;

//     static Utilities()
//     {
//         BASE = CUSTOM_BASE_TABLE.Length;
//         if (BASE < 2)
//             throw new InvalidOperationException($"Table must have at least 2 characters, has {BASE}");

//         CUSTOM_BASE_MAP = new Dictionary<char, int>(BASE);
//         for (int i = 0; i < BASE; i++)
//             CUSTOM_BASE_MAP[CUSTOM_BASE_TABLE[i]] = i;
//     }

//     /// <summary>
//     /// Encodes a byte array into a compact string representation using the custom base.
//     /// Avoids BigInteger for faster performance and zero extra dependencies.
//     /// </summary>
//     public static string EncodeCustomBase(byte[] data)
//     {
//         if (data == null)
//             throw new ArgumentNullException(nameof(data));

//         if (data.Length == 0)
//             return string.Empty;

//         byte[] input = (byte[])data.Clone();
//         int start = 0;

//         // Count leading zeros
//         while (start < input.Length && input[start] == 0)
//             start++;

//         var sb = new StringBuilder();

//         // Repeatedly divide by BASE, capturing remainders
//         while (start < input.Length)
//         {
//             int remainder = 0;
//             for (int i = start; i < input.Length; i++)
//             {
//                 int value = (remainder << 8) | input[i];
//                 input[i] = (byte)(value / BASE);
//                 remainder = value % BASE;
//             }

//             // Skip leading zeros as they accumulate
//             while (start < input.Length && input[start] == 0)
//                 start++;

//             sb.Insert(0, CUSTOM_BASE_TABLE[remainder]);
//         }

//         // Preserve original leading zeros
//         int zeroCount = 0;
//         while (zeroCount < data.Length && data[zeroCount] == 0)
//             zeroCount++;

//         if (zeroCount > 0)
//             sb.Insert(0, new string(CUSTOM_BASE_TABLE[0], zeroCount));

//         return sb.ToString();
//     }

//     /// <summary>
//     /// Decodes a custom base string back into its original byte array.
//     /// Also avoids BigInteger and performs manual base conversion.
//     /// </summary>
//     public static byte[] DecodeCustomBase(string data)
//     {
//         if (data == null)
//             throw new ArgumentNullException(nameof(data));

//         if (data.Length == 0)
//             return Array.Empty<byte>();

//         // Convert each character to its base digit
//         int[] input = new int[data.Length];
//         int inputLen = 0;
//         for (int i = 0; i < data.Length; i++)
//         {
//             if (CUSTOM_BASE_MAP.TryGetValue(data[i], out int val))
//                 input[inputLen++] = val;
//             // Skip invalid characters silently (optional)
//         }

//         int start = 0;
//         while (start < inputLen && input[start] == 0)
//             start++;

//         List<byte> output = new List<byte>();

//         // Repeatedly divide the base-N number by 256
//         while (start < inputLen)
//         {
//             int remainder = 0;
//             for (int i = start; i < inputLen; i++)
//             {
//                 int value = remainder * BASE + input[i];
//                 input[i] = value >> 8; // divide by 256
//                 remainder = value & 0xFF;
//             }

//             while (start < inputLen && input[start] == 0)
//                 start++;

//             output.Insert(0, (byte)remainder);
//         }

//         // Preserve leading zeros from the encoded string
//         int zeroCount = 0;
//         while (zeroCount < data.Length && data[zeroCount] == CUSTOM_BASE_TABLE[0])
//             zeroCount++;

//         if (zeroCount > 0)
//             output.InsertRange(0, new byte[zeroCount]);

//         return output.ToArray();
//     }
// }
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
