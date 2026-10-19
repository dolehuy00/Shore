using System.Security.Cryptography;

namespace Shorekeeper.Core.Audio;

/// <summary>
/// The optional password of "Phát âm thanh" (docs/05-protocol.md §4.7). The source keeps only a salted PBKDF2-SHA256
/// hash, "base64(salt 16 bytes · hash 32 bytes)", so settings.json never holds the password itself.
/// </summary>
public static class AudioPassword
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        byte[] stored = new byte[SaltSize + HashSize];
        RandomNumberGenerator.Fill(stored.AsSpan(0, SaltSize));
        Derive(password, stored.AsSpan(0, SaltSize), stored.AsSpan(SaltSize));
        return Convert.ToBase64String(stored);
    }

    /// <returns>False also when <paramref name="hash"/> is not one this class made.</returns>
    public static bool Verify(string password, string hash)
    {
        byte[] stored = new byte[SaltSize + HashSize];
        if (!Convert.TryFromBase64String(hash, stored, out int written) || written != stored.Length)
        {
            return false;
        }

        Span<byte> derived = stackalloc byte[HashSize];
        Derive(password, stored.AsSpan(0, SaltSize), derived);
        return CryptographicOperations.FixedTimeEquals(derived, stored.AsSpan(SaltSize));
    }

    private static void Derive(string password, ReadOnlySpan<byte> salt, Span<byte> destination) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, destination, Iterations, HashAlgorithmName.SHA256);
}
