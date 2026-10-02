using System.Net;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Engine.Tests;

public sealed class PeerDirectoryTests
{
    private static readonly IPAddress Source = IPAddress.Parse("10.1.1.40");

    private readonly ManualTimeProvider time = new();
    private readonly PeerDirectory directory;
    private readonly List<PeerChange> changes = [];

    public PeerDirectoryTests()
    {
        directory = new PeerDirectory(time);
        directory.Changed += (_, change) => changes.Add(change);
    }

    [Fact]
    public void First_packet_adds_the_peer()
    {
        directory.Observe(Packet("Mai"), Source);

        PeerChange change = Assert.Single(changes);
        Assert.Equal(PeerChangeKind.Added, change.Kind);
        Assert.Equal("Mai", change.Peer.DisplayName);
        Assert.Equal(Source, change.Peer.Address);
        Assert.Equal(PeerState.Online, change.Peer.State);
    }

    [Fact]
    public void Repeated_heartbeats_do_not_raise_changes()
    {
        directory.Observe(Packet("Mai"), Source);
        time.Advance(TimeSpan.FromSeconds(5));
        directory.Observe(Packet("Mai"), Source);

        Assert.Single(changes);
    }

    [Fact]
    public void Status_or_address_change_raises_update()
    {
        directory.Observe(Packet("Mai"), Source);
        directory.Observe(Packet("Mai") with { Status = PresenceStatus.Busy }, Source);
        directory.Observe(Packet("Mai") with { Status = PresenceStatus.Busy }, IPAddress.Parse("10.1.1.41"));

        Assert.Equal([PeerChangeKind.Added, PeerChangeKind.Updated, PeerChangeKind.Updated], changes.Select(c => c.Kind));
        Assert.True(changes[^1].Peer.IsBusy);
    }

    [Fact]
    public void Silent_peer_becomes_stale_then_is_removed()
    {
        directory.Observe(Packet("Mai"), Source);

        time.Advance(PeerDirectory.StaleAfter - TimeSpan.FromSeconds(1));
        directory.Sweep();
        Assert.Single(changes);

        time.Advance(TimeSpan.FromSeconds(1));
        directory.Sweep();
        Assert.Equal(PeerState.Stale, changes[^1].Peer.State);

        time.Advance(PeerDirectory.RemoveAfter - PeerDirectory.StaleAfter);
        directory.Sweep();
        Assert.Equal(PeerChangeKind.Removed, changes[^1].Kind);
        Assert.Empty(directory.Snapshot());
    }

    [Fact]
    public void Stale_peer_comes_back_online_when_heard_again()
    {
        directory.Observe(Packet("Mai"), Source);
        time.Advance(PeerDirectory.StaleAfter);
        directory.Sweep();

        directory.Observe(Packet("Mai"), Source);

        Assert.Equal(PeerState.Online, changes[^1].Peer.State);
        Assert.Equal(PeerChangeKind.Updated, changes[^1].Kind);
    }

    [Fact]
    public void Bye_removes_the_peer_immediately()
    {
        PresencePacket packet = Packet("Mai");
        directory.Observe(packet, Source);

        directory.Remove(DeviceId.Parse(packet.Id));

        Assert.Equal(PeerChangeKind.Removed, changes[^1].Kind);
        Assert.Empty(directory.Snapshot());
    }

    [Fact]
    public void Missing_name_falls_back_to_host_name()
    {
        directory.Observe(Packet("") with { Host = "PC-QA-07" }, Source);

        Assert.Equal("PC-QA-07", changes[0].Peer.DisplayName);
    }

    [Fact]
    public void Relayed_presence_does_not_replace_a_peer_heard_directly()
    {
        directory.Observe(Packet("Mai"), Source, 47470);
        directory.ObserveRelayed(Packet("Mai qua cầu nối"), IPAddress.Parse("10.9.9.9"));

        PeerInfo peer = Assert.Single(directory.Snapshot());
        Assert.Equal(PeerSource.Direct, peer.Source);
        Assert.Equal(Source, peer.Address);
        Assert.Equal(47470, peer.DiscoveryPort);
    }

    [Fact]
    public void Relayed_presence_takes_over_once_direct_packets_stop()
    {
        directory.Observe(Packet("Mai"), Source, 47470);
        time.Advance(PeerDirectory.StaleAfter);
        directory.Sweep();

        directory.ObserveRelayed(Packet("Mai"), Source);

        PeerInfo peer = Assert.Single(directory.Snapshot());
        Assert.Equal(PeerSource.Bridge, peer.Source);
        Assert.Equal(PeerState.Online, peer.State);
    }

    [Fact]
    public void Relayed_peer_is_kept_longer_than_one_heard_directly()
    {
        directory.ObserveRelayed(Packet("Mai"), Source);

        time.Advance(PeerDirectory.RemoveAfter);
        directory.Sweep();
        Assert.Equal(PeerState.Online, Assert.Single(directory.Snapshot()).State);

        time.Advance(PeerDirectory.RelayedStaleAfter - PeerDirectory.RemoveAfter);
        directory.Sweep();
        Assert.Equal(PeerState.Stale, Assert.Single(directory.Snapshot()).State);

        time.Advance(PeerDirectory.RelayedRemoveAfter - PeerDirectory.RelayedStaleAfter);
        directory.Sweep();
        Assert.Empty(directory.Snapshot());
    }

    [Fact]
    public void Bridge_dropping_a_peer_keeps_it_when_heard_directly()
    {
        DeviceId id = DeviceId.Parse(Packet("Mai").Id);
        directory.Observe(Packet("Mai"), Source, 47470);

        directory.RemoveRelayed(id);
        Assert.Single(directory.Snapshot());

        time.Advance(PeerDirectory.StaleAfter);
        directory.Sweep();
        directory.ObserveRelayed(Packet("Mai"), Source);
        directory.RemoveRelayed(id);
        Assert.Empty(directory.Snapshot());
    }

    [Fact]
    public void Bridge_capability_is_recorded()
    {
        directory.Observe(Packet("Mai") with { Capabilities = [PresenceCapabilities.Bridge] }, Source);

        Assert.True(Assert.Single(directory.Snapshot()).IsBridge);
    }

    private static PresencePacket Packet(string name) => new()
    {
        Type = PresencePacketTypes.Heartbeat,
        Id = new string('m', 52),
        Name = name,
        Host = "PC-QA-07",
    };
}
