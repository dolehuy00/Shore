using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Shorekeeper.Core.Identity;

namespace A0;

/// <summary>Same device certificate as Shorekeeper.Engine's CertificateFactory, loaded back from PKCS#12 like IdentityStore does.</summary>
internal static class DeviceCert
{
    public static X509Certificate2 Create()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Shorekeeper Device", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    }

    public static DeviceId IdOf(X509Certificate certificate)
    {
        using X509Certificate2 full = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return DeviceId.FromSubjectPublicKeyInfo(full.PublicKey.ExportSubjectPublicKeyInfo());
    }
}
