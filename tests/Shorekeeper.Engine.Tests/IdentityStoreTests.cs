using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Identity;

namespace Shorekeeper.Engine.Tests;

public sealed class IdentityStoreTests : IDisposable
{
    private readonly TempAppFolder folder = new();
    private readonly XorSecretProtector protector = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void First_run_creates_protected_identity_file()
    {
        using DeviceIdentity identity = CreateStore().LoadOrCreate();

        Assert.True(File.Exists(folder.Paths.IdentityFile));
        Assert.Equal(1, protector.ProtectCalls);
        Assert.True(identity.Certificate.HasPrivateKey);
        Assert.Equal(DeviceId.Length, identity.DeviceId.Value.Length);
    }

    [Fact]
    public void Second_run_loads_the_same_identity()
    {
        DeviceId first;
        using (DeviceIdentity identity = CreateStore().LoadOrCreate())
        {
            first = identity.DeviceId;
        }

        using DeviceIdentity reloaded = CreateStore().LoadOrCreate();

        Assert.Equal(first, reloaded.DeviceId);
        Assert.Equal(1, protector.ProtectCalls);
        Assert.Equal(1, protector.UnprotectCalls);
        Assert.True(reloaded.Certificate.HasPrivateKey);
    }

    [Fact]
    public void DeviceId_is_derived_from_the_certificate_public_key()
    {
        using DeviceIdentity identity = CreateStore().LoadOrCreate();

        DeviceId expected = DeviceId.FromSubjectPublicKeyInfo(identity.Certificate.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.Equal(expected, identity.DeviceId);
    }

    [Fact]
    public void Certificate_is_valid_for_client_and_server_tls()
    {
        using DeviceIdentity identity = CreateStore().LoadOrCreate();
        X509Certificate2 cert = identity.Certificate;

        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.2");
        Assert.True(cert.NotAfter > DateTime.Now.AddYears(19));
        Assert.Equal("ECC", cert.PublicKey.Oid.FriendlyName);
    }

    [Fact]
    public void Unreadable_file_is_backed_up_and_a_new_identity_is_created()
    {
        File.WriteAllBytes(folder.Paths.IdentityFile, [1, 2, 3, 4, 5, 6, 7, 8]);

        using DeviceIdentity identity = CreateStore().LoadOrCreate();

        Assert.True(identity.Certificate.HasPrivateKey);
        Assert.Single(Directory.GetFiles(folder.Paths.DataDirectory, "identity.pfx.dpapi.broken-*"));
    }

    [Fact]
    public void Identity_protected_by_another_user_is_replaced()
    {
        using (CreateStore().LoadOrCreate())
        {
        }

        // Simulate DPAPI failing (profile copied to another machine/user).
        var otherUser = new IdentityStore(folder.Paths, new ThrowingProtector(), TimeProvider.System, NullLogger<IdentityStore>.Instance);
        using DeviceIdentity identity = otherUser.LoadOrCreate();

        Assert.True(identity.Certificate.HasPrivateKey);
        Assert.Single(Directory.GetFiles(folder.Paths.DataDirectory, "identity.pfx.dpapi.broken-*"));
    }

    private IdentityStore CreateStore() =>
        new(folder.Paths, protector, TimeProvider.System, NullLogger<IdentityStore>.Instance);

    private sealed class ThrowingProtector : Core.Platform.ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) =>
            throw new System.Security.Cryptography.CryptographicException("Key not valid for use in specified state.");
    }
}
