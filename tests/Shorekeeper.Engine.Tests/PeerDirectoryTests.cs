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

    private static PresencePacket Packet(string name) => new()
    {
        Type = PresencePacketTypes.Heartbeat,
        Id = new string('m', 52),
        Name = name,
        Host = "PC-QA-07",
    };
}
