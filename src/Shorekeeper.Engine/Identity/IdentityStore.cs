using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Engine.Identity;

/// <summary>
/// Loads the device identity, creating it on first run.
/// File format: "SKID" magic, 1-byte version, then the PKCS#12 blob protected by <see cref="ISecretProtector"/>.
/// </summary>
public sealed class IdentityStore(
    AppPaths paths,
    ISecretProtector protector,
    TimeProvider timeProvider,
    ILogger<IdentityStore> logger)
{
    private const uint Magic = 0x44494B53; // "SKID" little-endian
    private const byte FormatVersion = 1;
    private const int HeaderLength = 5;

    public DeviceIdentity LoadOrCreate()
    {
        string path = paths.IdentityFile;
        if (File.Exists(path))
        {
            try
            {
                DeviceIdentity identity = Load(path);
                logger.LogInformation("Loaded device identity {DeviceId}", identity.DeviceId);
                return identity;
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or ArgumentException)
            {
                // Typical cause: the profile was copied to another machine/user, so DPAPI cannot decrypt it.
                // Keep the old file for diagnosis and start with a fresh identity; contacts must reconnect.
                string backup = $"{path}.broken-{timeProvider.GetUtcNow():yyyyMMddHHmmss}";
                File.Move(path, backup);
                logger.LogWarning(ex, "Device identity could not be read and was moved to {Backup}. A new identity will be created.", backup);
            }
        }

        DeviceIdentity created = Create(path);
        logger.LogInformation("Created new device identity {DeviceId}", created.DeviceId);
        return created;
    }

    private DeviceIdentity Load(string path)
    {
        byte[] content = File.ReadAllBytes(path);
        if (content.Length <= HeaderLength
            || BinaryPrimitives.ReadUInt32LittleEndian(content) != Magic
            || content[4] != FormatVersion)
        {
            throw new InvalidDataException("Identity file has an unknown format.");
        }

        byte[] pkcs12 = protector.Unprotect(content[HeaderLength..]);
        try
        {
            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(pkcs12, password: null, X509KeyStorageFlags.UserKeySet);
            return new DeviceIdentity(certificate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private DeviceIdentity Create(string path)
    {
        using X509Certificate2 generated = CertificateFactory.CreateDeviceCertificate(timeProvider.GetUtcNow());
        byte[] pkcs12 = generated.Export(X509ContentType.Pkcs12);
        try
        {
            byte[] protectedBlob = protector.Protect(pkcs12);
            byte[] content = new byte[HeaderLength + protectedBlob.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(content, Magic);
            content[4] = FormatVersion;
            protectedBlob.CopyTo(content, HeaderLength);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);

            // Reload from the persisted bytes so the in-memory key is exactly what we will load next time.
            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(pkcs12, password: null, X509KeyStorageFlags.UserKeySet);
            return new DeviceIdentity(certificate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }
}
