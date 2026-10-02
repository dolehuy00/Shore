using System.Net;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Engine.Tests;

/// <summary>Seeing peers in other subnets over the API: PEX between contacts, and the Bridge role (docs/03-discovery-presence.md §5, §7).</summary>
public sealed class CrossSubnetTests
{
    private static readonly IPAddress OtherSubnet = IPAddress.Parse("10.1.5.20");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Contact_shares_the_peers_it_hears_and_they_get_probed()
    {
        await using TestNode a = await TestNode.StartAsync("A");
        await using TestNode b = await TestNode.StartAsync("B");
        await using TestNode c = await TestNode.StartAsync("C");
        await a.ConnectAsync(b);
        // B hears C on UDP (TestNode has no UDP discovery, so say so directly).
        b.Directory.Observe(Heartbeat(c), OtherSubnet, 47470);

        int learned = await a.Get<PexService>().ExchangeAsync(b.Id, Ct);

        Assert.Equal(1, learned);
        Assert.Contains(new IPEndPoint(OtherSubnet, 47470), a.Get<ProbeHints>().GetActive());
        // Not shown until C answers a probe itself.
        Assert.DoesNotContain(a.Directory.Snapshot(), p => p.DeviceId == c.Id);
    }

    [Fact]
    public async Task Peers_only_known_through_a_bridge_or_the_caller_itself_are_not_shared()
    {
        await using TestNode a = await TestNode.StartAsync("A");
        await using TestNode b = await TestNode.StartAsync("B");
        await using TestNode c = await TestNode.StartAsync("C");
        await a.ConnectAsync(b);
        b.Directory.ObserveRelayed(Heartbeat(c), OtherSubnet);
        b.Directory.Observe(Heartbeat(a), IPAddress.Loopback, 47470);

        Assert.Equal(0, await a.Get<PexService>().ExchangeAsync(b.Id, Ct));
    }

    [Fact]
    public async Task Peers_registered_with_a_bridge_see_each_other_until_one_leaves()
    {
        await using TestNode bridge = await TestNode.StartAsync("Cầu nối", "\"enableBridge\": true");
        string useBridge = $"\"bridgeAddresses\": [\"127.0.0.1:{bridge.ApiPort}\"]";
        await using TestNode a = await TestNode.StartAsync("A", useBridge);
        TestNode c = await TestNode.StartAsync("C", useBridge);

        await WaitUntil(() => Sees(a, c) && Sees(c, a) && Sees(a, bridge) && Sees(bridge, a) && Sees(bridge, c), TimeSpan.FromSeconds(15));

        PeerInfo seen = a.Directory.Snapshot().Single(p => p.DeviceId == c.Id);
        Assert.Equal(PeerSource.Bridge, seen.Source);
        Assert.Equal("C", seen.DisplayName);
        Assert.Equal(c.ApiPort, seen.ApiPort);
        Assert.True(a.Directory.Snapshot().Single(p => p.DeviceId == bridge.Id).IsBridge);
        Assert.True(a.Get<BridgeClient>().GetStatus().Single().IsConnected);

        // Closing the app unregisters, so others drop it right away instead of after the lease.
        DeviceId cId = c.Id;
        await c.DisposeAsync();
        await WaitUntil(() => a.Directory.Snapshot().All(p => p.DeviceId != cId), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Bridge_that_goes_away_is_reported()
    {
        TestNode bridge = await TestNode.StartAsync("Cầu nối", "\"enableBridge\": true");
        await using TestNode a = await TestNode.StartAsync("A", $"\"bridgeAddresses\": [\"127.0.0.1:{bridge.ApiPort}\"]");
        BridgeClient client = a.Get<BridgeClient>();
        await WaitUntil(() => client.GetStatus() is [{ IsConnected: true }], TimeSpan.FromSeconds(10));

        await bridge.DisposeAsync();

        await WaitUntil(() => client.GetStatus() is [{ IsConnected: false, Error: not null }], TimeSpan.FromSeconds(10));
    }

    private static bool Sees(TestNode node, TestNode other) => node.Directory.Snapshot().Any(p => p.DeviceId == other.Id);

    private static PresencePacket Heartbeat(TestNode node) =>
        new() { Type = PresencePacketTypes.Heartbeat, Id = node.Id.Value, Name = "peer", Port = node.ApiPort };

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Condition not met within {timeout.TotalSeconds}s.");
            await Task.Delay(100, Ct);
        }
    }
}
