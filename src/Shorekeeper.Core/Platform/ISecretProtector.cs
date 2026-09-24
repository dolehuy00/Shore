namespace Shorekeeper.Core.Platform;

/// <summary>
/// Encrypts secrets at rest so only the current OS user can read them
/// (DPAPI on Windows; Keychain / libsecret on other platforms later).
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The data was protected by another user/machine or has been tampered with.
    /// </exception>
    byte[] Unprotect(byte[] protectedData);
}
