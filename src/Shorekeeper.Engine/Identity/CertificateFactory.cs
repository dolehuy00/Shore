using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Shorekeeper.Engine.Identity;

/// <summary>
/// Creates the self-signed device certificate used for mutual TLS.
/// Trust never comes from the certificate chain: peers pin the DeviceId
/// derived from the public key (docs/04-identity-security.md §2).
/// </summary>
internal static class CertificateFactory
{
    public const string SubjectName = "CN=Shorekeeper Device";
    public static readonly TimeSpan Validity = TimeSpan.FromDays(365 * 20);

    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    public static X509Certificate2 CreateDeviceCertificate(DateTimeOffset now)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(SubjectName, key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(ServerAuthOid), new Oid(ClientAuthOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        // Back-date slightly so peers with a small clock skew accept it immediately.
        return request.CreateSelfSigned(now.AddDays(-1), now.Add(Validity));
    }
}
