namespace Shorekeeper.Core.Encoding;

/// <summary>
/// RFC 4648 Base32, lower-case, without padding. Used for DeviceId so it is
/// case-insensitive, URL/file-name safe and free of look-alike digits (0/1/8/9).
/// </summary>
public static class Base32
{
    public const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static int GetEncodedLength(int byteCount) => (byteCount * 8 + 4) / 5;

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        Span<char> chars = stackalloc char[GetEncodedLength(data.Length)];
        int buffer = 0;
        int bitCount = 0;
        int index = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                chars[index++] = Alphabet[(buffer >> (bitCount - 5)) & 0x1F];
                bitCount -= 5;
            }
        }

        if (bitCount > 0)
        {
            chars[index++] = Alphabet[(buffer << (5 - bitCount)) & 0x1F];
        }

        return new string(chars[..index]);
    }

    public static bool IsValid(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!Alphabet.Contains(c))
            {
                return false;
            }
        }

        return true;
    }
}
