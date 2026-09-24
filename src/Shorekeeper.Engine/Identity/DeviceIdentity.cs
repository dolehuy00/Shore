using System.Security.Cryptography.X509Certificates;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Engine.Identity;

/// <summary>This device's certificate (with private key) and the DeviceId derived from it.</summary>
public sealed class DeviceIdentity : IDisposable
{
    public DeviceIdentity(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException("Device certificate must include its private key.", nameof(certificate));
        }

        Certificate = certificate;
        DeviceId = FromCertificate(certificate);
    }

    public X509Certificate2 Certificate { get; }

    public DeviceId DeviceId { get; }

    public static DeviceId FromCertificate(X509Certificate2 certificate) =>
        DeviceId.FromSubjectPublicKeyInfo(certificate.PublicKey.ExportSubjectPublicKeyInfo());

    public void Dispose() => Certificate.Dispose();
}
