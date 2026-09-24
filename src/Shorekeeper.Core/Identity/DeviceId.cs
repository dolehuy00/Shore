using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Shorekeeper.Core.Encoding;

namespace Shorekeeper.Core.Identity;

/// <summary>
/// Stable device identifier: Base32(SHA-256(SubjectPublicKeyInfo)).
/// Bound to the device key, so it cannot be claimed without the private key.
/// See docs/04-identity-security.md §1.
/// </summary>
public readonly record struct DeviceId
{
    public const int Length = 52;

    private DeviceId(string value) => Value = value;

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Short, human-friendly form for UI, e.g. <c>k3f9-2mxa</c>.</summary>
    public string ShortForm => IsEmpty ? string.Empty : $"{Value[..4]}-{Value[4..8]}";

    public static DeviceId FromSubjectPublicKeyInfo(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(subjectPublicKeyInfo, hash);
        return new DeviceId(Base32.Encode(hash));
    }

    public static bool TryParse([NotNullWhen(true)] string? text, out DeviceId deviceId)
    {
        deviceId = default;
        if (text is null)
        {
            return false;
        }

        string normalized = text.Trim().ToLowerInvariant();
        if (normalized.Length != Length || !Base32.IsValid(normalized))
        {
            return false;
        }

        deviceId = new DeviceId(normalized);
        return true;
    }

    public static DeviceId Parse(string text) =>
        TryParse(text, out DeviceId id) ? id : throw new FormatException($"'{text}' is not a valid DeviceId.");

    public override string ToString() => Value ?? string.Empty;
}
