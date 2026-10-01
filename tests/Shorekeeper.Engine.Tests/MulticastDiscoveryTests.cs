using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Engine.Tests;

/// <summary>
/// Several discovery instances in one process, on loopback and a private port, so no packet reaches the real LAN.
/// </summary>
public sealed class MulticastDiscoveryTests : IAsyncDisposable
{
    private static readonly TimeSpan SeeEachOtherWithin = TimeSpan.FromSeconds(3);

    private readonly int port = FreePort.Udp();
    private readonly List<TestPeer> peers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (TestPeer peer in peers)
        {
            await peer.Discovery.StopAsync(CancellationToken.None);
            peer.Discovery.Dispose();
        }
    }

    [Fact]
    public async Task Three_peers_see_each_other_within_three_seconds()
    {
        TestPeer a = await StartPeer("A"), b = await StartPeer("B"), c = await StartPeer("C");

        await WaitUntil(() => peers.All(p => p.Directory.Snapshot().Count == 2), SeeEachOtherWithin);

        Assert.Equal(["B", "C"], Names(a));
        Assert.Equal(["A", "C"], Names(b));
        Assert.Equal(["A", "B"], Names(c));
    }

    [Fact]
    public async Task Stopping_a_peer_says_bye()
    {
        TestPeer a = await StartPeer("A");
        TestPeer b = await StartPeer("B");
        await WaitUntil(() => a.Directory.Snapshot().Count == 1, SeeEachOtherWithin);

        await b.Discovery.StopAsync(TestContext.Current.CancellationToken);

        await WaitUntil(() => a.Directory.Snapshot().Count == 0, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Hidden_peer_disappears_and_comes_back_when_online_again()
    {
        TestPeer a = await StartPeer("A");
        TestPeer b = await StartPeer("B");
        // B learns about A from A's reply, which is deliberately delayed by up to 500 ms.
        await WaitUntil(() => a.Directory.Snapshot().Count == 1 && b.Directory.Snapshot().Count == 1, SeeEachOtherWithin);

        b.Presence.Status = MyStatus.Hidden;
        await WaitUntil(() => a.Directory.Snapshot().Count == 0, TimeSpan.FromSeconds(3));

        // Hidden still sees others.
        Assert.Single(b.Directory.Snapshot());

        b.Presence.Status = MyStatus.Online;
        await WaitUntil(() => a.Directory.Snapshot().Count == 1, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Busy_status_is_visible_to_others()
    {
        TestPeer a = await StartPeer("A");
        TestPeer b = await StartPeer("B");
        await WaitUntil(() => a.Directory.Snapshot().Count == 1, SeeEachOtherWithin);

        b.Presence.Status = MyStatus.Busy;

        await WaitUntil(() => a.Directory.Snapshot().Single().IsBusy, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Peer_outside_multicast_reach_is_found_by_probe()
    {
        // A different port stands in for another subnet: B never hears A's multicast and vice versa.
        int otherPort = FreePort.Udp();
        while (otherPort == port)
        {
            otherPort = FreePort.Udp(); // the OS may hand the same free port out twice in a row
        }
        TestPeer a = await StartPeer("A");
        TestPeer b = await StartPeer("B", otherPort, multicast: false, probe: [new IPEndPoint(IPAddress.Loopback, port)]);

        // B probes A; A records B from the probe and replies, so both sides see each other.
        await WaitUntil(() => a.Directory.Snapshot().Count == 1 && b.Directory.Snapshot().Count == 1, SeeEachOtherWithin);

        Assert.Equal("B", a.Directory.Snapshot().Single().DisplayName);
        Assert.Equal("A", b.Directory.Snapshot().Single().DisplayName);
    }

    private async Task<TestPeer> StartPeer(string name, int? peerPort = null, bool multicast = true, IPEndPoint[]? probe = null)
    {
        var presence = new FakePresence(name);
        var directory = new PeerDirectory(TimeProvider.System);
        var discovery = new MulticastDiscovery(
            presence,
            directory,
            NullLogger<MulticastDiscovery>.Instance,
            () => peerPort ?? port,
            IPAddress.Loopback,
            () => multicast ? [new LocalInterface("Loopback", "test", IPAddress.Loopback, 8, null)] : [],
            new FixedProbeTargets(probe ?? []));

        var peer = new TestPeer(presence, directory, discovery);
        peers.Add(peer);
        await discovery.StartAsync(TestContext.Current.CancellationToken);
        return peer;
    }

    private static string[] Names(TestPeer peer) => [.. peer.Directory.Snapshot().Select(p => p.DisplayName).Order()];

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Condition not met within {timeout.TotalSeconds}s.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FixedProbeTargets(IReadOnlyList<IPEndPoint> targets) : IProbeTargetSource
    {
        public Task<IReadOnlyList<IPEndPoint>> GetTargetsAsync(CancellationToken cancellationToken) => Task.FromResult(targets);
    }

    private sealed record TestPeer(FakePresence Presence, PeerDirectory Directory, MulticastDiscovery Discovery);

    private sealed class FakePresence(string name) : ILocalPresence
    {
        private readonly string id = DeviceId.FromSubjectPublicKeyInfo(RandomNumberGenerator.GetBytes(32)).Value;
        private MyStatus status;

        public event EventHandler? Changed;

        public MyStatus Status
        {
            get => status;
            set
            {
                status = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public PresencePacket CreatePacket(string type) => new()
        {
            Type = type,
            Id = id,
            Name = name,
            Host = "TEST-" + name,
            Status = status == MyStatus.Busy ? PresenceStatus.Busy : PresenceStatus.Available,
        };
    }
}
