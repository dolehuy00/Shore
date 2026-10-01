using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Shorekeeper.Core.Identity;
using TextEncoding = System.Text.Encoding;

namespace Shorekeeper.Core.Trust;

/// <summary>
/// 6-digit code both sides show while connecting (docs/04-identity-security.md §4).
/// It depends on both DeviceIds as seen through TLS, so a man-in-the-middle makes the two screens differ.
/// </summary>
public static class PairingCode
{
    public const int NonceSize = 32;

    private static ReadOnlySpan<byte> Label => "sk-pair-v1"u8;

    public static string Compute(DeviceId a, DeviceId b, ReadOnlySpan<byte> requesterNonce, ReadOnlySpan<byte> responderNonce)
    {
        // Order the ids so both sides hash the same bytes regardless of who computes.
        (DeviceId first, DeviceId second) = string.CompareOrdinal(a.Value, b.Value) <= 0 ? (a, b) : (b, a);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Label);
        hash.AppendData(TextEncoding.ASCII.GetBytes(first.Value));
        hash.AppendData(TextEncoding.ASCII.GetBytes(second.Value));
        hash.AppendData(requesterNonce);
        hash.AppendData(responderNonce);

        uint value = BinaryPrimitives.ReadUInt32BigEndian(hash.GetHashAndReset()) % 1_000_000;
        string digits = value.ToString("D6", CultureInfo.InvariantCulture);
        return $"{digits[..3]} {digits[3..]}";
    }
}
