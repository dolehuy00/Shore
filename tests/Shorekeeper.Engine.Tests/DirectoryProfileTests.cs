using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Tests;

/// <summary>Active Directory level 1 with a fake directory (docs/13-active-directory.md §2).</summary>
public sealed class DirectoryProfileTests : IDisposable
{
    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private readonly TempAppFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task Department_and_picture_reach_other_peers()
    {
        var directory = new FakeDirectory(new DirectoryProfile("Lê Văn Huy", "Phòng Kỹ thuật", "Dev", "huy@corp.local", Photo));
        await using TestNode huy = await TestNode.StartAsync("Huy", directory: directory);
        await using TestNode mai = await TestNode.StartAsync("Mai");
        await WaitUntil(() => huy.Get<DirectoryProfileService>().AvatarTag is not null);

        PresencePacket packet = huy.Get<ILocalPresence>().CreatePacket(PresencePacketTypes.Heartbeat);
        Assert.Equal("Phòng Kỹ thuật", packet.Department);
        mai.Directory.Observe(packet, IPAddress.Loopback);

        PeerInfo seen = Assert.Single(mai.Directory.Snapshot());
        Assert.Equal("Phòng Kỹ thuật", seen.Department);
        Assert.Equal(Photo, await mai.Get<PeerAvatars>().GetAsync(seen));
    }

    [Fact]
    public async Task Peers_without_a_picture_answer_not_found()
    {
        await using TestNode huy = await TestNode.StartAsync("Huy");
        await using TestNode mai = await TestNode.StartAsync("Mai");
        // Claims a picture it does not have.
        mai.Directory.Observe(huy.Get<ILocalPresence>().CreatePacket(PresencePacketTypes.Heartbeat) with { AvatarTag = "0a1b2c3d" }, IPAddress.Loopback);

        Assert.Null(await mai.Get<PeerAvatars>().GetAsync(Assert.Single(mai.Directory.Snapshot())));
    }

    [Fact]
    public async Task Last_profile_is_used_when_the_directory_cannot_be_reached()
    {
        var online = new FakeDirectory(new DirectoryProfile("Lê Văn Huy", "Phòng Kỹ thuật", null, null, Photo));
        await RunOnceAsync(online, profile => profile.AvatarTag is not null);

        SettingsService settings = await RunOnceAsync(new FakeDirectory(null), profile => profile.Profile is not null, service =>
        {
            Assert.Equal("Phòng Kỹ thuật", service.Department);
            Assert.Equal(Photo, service.Avatar);
        });
        Assert.Equal("Lê Văn Huy", settings.Current.DisplayName);
    }

    private async Task<SettingsService> RunOnceAsync(
        IDirectoryInfo directory, Func<DirectoryProfileService, bool> ready, Action<DirectoryProfileService>? check = null)
    {
        var settings = new SettingsService(folder.Paths, new SettingsStore(folder.Paths, TimeProvider.System, NullLogger<SettingsStore>.Instance), NullPolicyProvider.Instance);
        settings.Load();
        using var service = new DirectoryProfileService(directory, folder.Paths, settings, TimeProvider.System, NullLogger<DirectoryProfileService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => ready(service));
        check?.Invoke(service);
        await service.StopAsync(TestContext.Current.CancellationToken);
        return settings;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeDirectory(DirectoryProfile? profile) : IDirectoryInfo
    {
        public DirectoryProfile? GetCurrentUser() => profile;
    }
}
