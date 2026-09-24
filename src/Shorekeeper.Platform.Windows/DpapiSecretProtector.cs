using System.Security.Cryptography;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

/// <summary>DPAPI: data can only be decrypted by the same Windows user (or machine, for services).</summary>
public sealed class DpapiSecretProtector(DataProtectionScope scope = DataProtectionScope.CurrentUser) : ISecretProtector
{
    private static readonly byte[] Entropy = "Shorekeeper.SecretProtector.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, scope);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, scope);
}
