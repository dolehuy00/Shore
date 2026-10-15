using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Hosting;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

public sealed class IdentityBackupTests : IDisposable
{
    private const string Password = "mat-khau-dai";

    private static readonly DeviceId Mai = DeviceId.Parse(new string('a', DeviceId.Length));
    private static readonly DeviceId Spammer = DeviceId.Parse(new string('b', DeviceId.Length));

    private readonly TempAppFolder oldPc = new();
    private readonly TempAppFolder newPc = new();

    public void Dispose()
    {
        oldPc.Dispose();
        newPc.Dispose();
    }

    [Fact]
    public async Task Restore_on_another_pc_keeps_the_identity_contacts_and_groups()
    {
        string file = Path.Combine(oldPc.Root, "huy" + IdentityBackup.FileExtension);
        DeviceId original;
        await using (EngineHost host = await EngineHost.StartAsync(oldPc))
        {
            original = host.DeviceId;
            var trust = host.Get<TrustStore>();
            await trust.SetTrustedAsync(Mai, "Mai", "PC-QA-07", Ct);
            await trust.SetAliasAsync(Mai, "Mai QA", Ct);
            await trust.SetAutoAcceptAsync(Mai, 1024, Ct);
            await trust.BlockAsync(Spammer, "Spam", "PC-X", Ct);
            await host.Get<GroupStore>().SaveAsync(
                new Group(new string('c', 32), "Build QA", original, GroupRole.Host, [new GroupMember(original, "Huy", true), new GroupMember(Mai, "Mai", false)]), Ct);

            await host.Get<IdentityBackup>().ExportAsync(file, Password, "Huy", Ct);
        }

        await using (EngineHost host = await EngineHost.StartAsync(newPc))
        {
            Assert.NotEqual(original, host.DeviceId);
            OpenedBackup backup = IdentityBackup.Open(await File.ReadAllBytesAsync(file, Ct), Password);
            Assert.Equal(new BackupSummary(original, "Huy", backup.Summary.CreatedAt, 2, 1), backup.Summary);
            host.Get<IdentityBackup>().StageRestore(backup);
        }

        await using (EngineHost restarted = await EngineHost.StartAsync(newPc))
        {
            Assert.Equal(original, restarted.DeviceId);
            Assert.True(restarted.Get<ShorekeeperEngine>().Identity.Certificate.HasPrivateKey);

            var trust = restarted.Get<TrustStore>();
            Assert.Equal(new PeerRecord(Mai, "Mai", "PC-QA-07", "Mai QA", TrustLevel.Trusted, 1024), trust.Get(Mai));
            Assert.Equal(TrustLevel.Blocked, trust.GetLevel(Spammer));

            Group group = Assert.Single(restarted.Get<GroupStore>().GetAll());
            Assert.Equal(("Build QA", GroupRole.Host, 2), (group.Name, group.Role, group.Members.Count));
        }

        Assert.False(File.Exists(newPc.Paths.PendingRestoreFile));
        Assert.Single(Directory.GetFiles(newPc.Paths.DataDirectory, "identity.pfx.dpapi.replaced-*"));
    }

    [Fact]
    public async Task Wrong_password_or_a_changed_file_is_rejected()
    {
        string file = Path.Combine(oldPc.Root, "huy" + IdentityBackup.FileExtension);
        await using (EngineHost host = await EngineHost.StartAsync(oldPc))
        {
            await host.Get<IdentityBackup>().ExportAsync(file, Password, "Huy", Ct);
        }

        byte[] content = await File.ReadAllBytesAsync(file, Ct);
        Assert.ThrowsAny<CryptographicException>(() => IdentityBackup.Open(content, "mat-khau-sai"));

        content[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => IdentityBackup.Open(content, Password));

        Assert.Throws<InvalidDataException>(() => IdentityBackup.Open("not a backup"u8.ToArray(), Password));
    }

    [Fact]
    public async Task Short_passwords_are_refused()
    {
        await using EngineHost host = await EngineHost.StartAsync(oldPc);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            host.Get<IdentityBackup>().ExportAsync(Path.Combine(oldPc.Root, "x.skbackup"), "1234567", "Huy", Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Just the engine (no API or discovery) on a data folder, like one start of the app.</summary>
    private sealed class EngineHost : IAsyncDisposable
    {
        private readonly IHost host;

        private EngineHost(IHost host) => this.host = host;

        public DeviceId DeviceId => Get<ShorekeeperEngine>().Identity.DeviceId;

        public static async Task<EngineHost> StartAsync(TempAppFolder folder)
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddLogging();
            builder.Services.AddShorekeeperEngine(folder.Paths);
            builder.Services.AddSingleton<ISecretProtector, XorSecretProtector>();
            IHost host = builder.Build();
            await host.StartAsync(Ct);
            return new EngineHost(host);
        }

        public T Get<T>()
            where T : notnull => host.Services.GetRequiredService<T>();

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(Ct);
            host.Dispose();
        }
    }
}
