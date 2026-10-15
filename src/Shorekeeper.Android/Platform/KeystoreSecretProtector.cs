using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// Protects the device key with an AES key kept in the Android Keystore (it never leaves the secure hardware or
/// the system service). Stored format: 12-byte IV, then the AES-GCM ciphertext with its tag.
/// </summary>
public sealed class KeystoreSecretProtector : ISecretProtector
{
    private const string KeyStoreName = "AndroidKeyStore";
    private const string Alias = "shorekeeper-identity";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvLength = 12;
    private const int TagBits = 128;

    public byte[] Protect(byte[] plaintext)
    {
        using Cipher cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        byte[] iv = cipher.GetIV()!;
        byte[] sealedData = cipher.DoFinal(plaintext)!;
        return [.. iv, .. sealedData];
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length <= IvLength)
        {
            throw new System.Security.Cryptography.CryptographicException("Protected data is too short.");
        }

        try
        {
            using Cipher cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, protectedData[..IvLength]));
            return cipher.DoFinal(protectedData[IvLength..])!;
        }
        catch (GeneralSecurityException ex)
        {
            // Wrong key (app data restored onto another phone) or tampered data: the engine starts with a new identity.
            throw new System.Security.Cryptography.CryptographicException("Protected data cannot be decrypted.", ex);
        }
    }

    private static IKey GetOrCreateKey()
    {
        KeyStore store = KeyStore.GetInstance(KeyStoreName)!;
        store.Load(null);
        if (store.GetKey(Alias, null) is { } existing)
        {
            return existing;
        }

        KeyGenerator generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }
}
