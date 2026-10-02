using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Tests;

/// <summary>The Bridge's registry: leases, versions and long-poll (docs/03-discovery-presence.md §7).</summary>
public sealed class BridgeRegistryTests : IDisposable
{
    private static readonly IPAddress Address = IPAddress.Parse("10.1.5.20");

    private readonly TempAppFolder folder = new();
    private readonly ManualTimeProvider time = new();
    private readonly SettingsService settings;
    private readonly BridgeRegistry registry;
    private readonly DeviceId a = NewId(), b = NewId();

    public BridgeRegistryTests()
    {
        File.WriteAllText(folder.Paths.SettingsFile, """{ "enableBridge": true }""");
        settings = new SettingsService(folder.Paths, new SettingsStore(folder.Paths, time, NullLogger<SettingsStore>.Instance), NullPolicyProvider.Instance);
        settings.Load();
        registry = new BridgeRegistry(settings, time);
    }

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task First_read_returns_everyone_then_only_changes()
    {
        Register(a);
        Register(b);

        BridgePeersResponse first = await Read(0);
        Assert.True(first.Full);
        Assert.Equal(2, first.Peers.Count);

        Register(a, Body() with { Status = PresenceStatus.Busy });
        BridgePeersResponse second = await Read(first.Version);
        Assert.False(second.Full);
        Assert.Equal(PresenceStatus.Busy, Assert.Single(second.Peers).Status);

        registry.Unregister(b);
        BridgePeersResponse third = await Read(second.Version);
        Assert.Empty(third.Peers);
        Assert.Equal([b.Value], third.Removed);
    }

    [Fact]
    public async Task Renewal_is_not_a_change()
    {
        Register(a);
        BridgePeersResponse first = await Read(0);

        time.Advance(TimeSpan.FromSeconds(20));
        Register(a);

        BridgePeersResponse second = await Read(first.Version);
        Assert.Equal(first.Version, second.Version);
        Assert.Empty(second.Peers);
    }

    [Fact]
    public async Task Peer_that_stops_renewing_is_removed()
    {
        Register(a);
        BridgePeersResponse first = await Read(0);

        time.Advance(TimeSpan.FromSeconds(30));
        Register(b);
        time.Advance(BridgeRegistry.Lease - TimeSpan.FromSeconds(30));

        BridgePeersResponse second = await Read(first.Version);
        Assert.Equal([a.Value], second.Removed);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task Reader_too_far_behind_gets_everything_again()
    {
        Register(a);
        BridgePeersResponse first = await Read(0);
        registry.Unregister(a);

        time.Advance(BridgeRegistry.RemovalRetention + TimeSpan.FromSeconds(1));
        Register(b);

        BridgePeersResponse second = await Read(first.Version);
        Assert.True(second.Full);
        Assert.Equal(b.Value, Assert.Single(second.Peers).DeviceId);
    }

    [Fact]
    public async Task Waiting_reader_is_woken_by_a_change()
    {
        BridgePeersResponse first = await Read(0);
        Task<BridgePeersResponse?> waiting = registry.GetPeersAsync(first.Version, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        Register(a);

        BridgePeersResponse? woken = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(a.Value, Assert.Single(woken!.Peers).DeviceId);
    }

    [Fact]
    public void Registration_records_the_address_the_caller_came_from()
    {
        (BridgeRegisterResult result, BridgeRegistered? response) = registry.Register(a, Address.MapToIPv6(), Body());

        Assert.Equal(BridgeRegisterResult.Registered, result);
        Assert.Equal("10.1.5.20", response!.ObservedAddr);
        Assert.Equal(60, response.LeaseSeconds);
    }

    [Fact]
    public void Invalid_registration_is_refused()
    {
        Assert.Equal(BridgeRegisterResult.Invalid, registry.Register(a, Address, Body() with { Name = new string('x', 65) }).Result);
        Assert.Equal(BridgeRegisterResult.Invalid, registry.Register(a, Address, Body() with { Status = "sleeping" }).Result);
        Assert.Equal(BridgeRegisterResult.Invalid, registry.Register(a, Address, Body() with { Port = 0 }).Result);
    }

    [Fact]
    public async Task Nothing_is_served_when_the_role_is_off()
    {
        settings.Update(s => s with { EnableBridge = false });

        Assert.Equal(BridgeRegisterResult.Disabled, registry.Register(a, Address, Body()).Result);
        Assert.Null(await registry.GetPeersAsync(0, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    private void Register(DeviceId id, BridgeRegistration? body = null) =>
        Assert.Equal(BridgeRegisterResult.Registered, registry.Register(id, Address, body ?? Body()).Result);

    private async Task<BridgePeersResponse> Read(long since) =>
        (await registry.GetPeersAsync(since, TimeSpan.Zero, TestContext.Current.CancellationToken))!;

    private static BridgeRegistration Body() => new("Mai", "PC-QA-07", "windows", "0.1.0", PresenceStatus.Available, 47471);

    private static DeviceId NewId() => DeviceId.FromSubjectPublicKeyInfo(RandomNumberGenerator.GetBytes(32));
}
