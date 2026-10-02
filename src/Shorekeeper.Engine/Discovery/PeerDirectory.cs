using System.Net;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Engine.Discovery;

public enum PeerState
{
    Online,

    /// <summary>No presence for a while; probably gone, kept briefly in case it was a hiccup.</summary>
    Stale,
}

public enum PeerSource
{
    /// <summary>The peer's own presence packets reach us (multicast or unicast probe/reply).</summary>
    Direct,

    /// <summary>Only known through a Bridge's registry; the peer may not hear us directly.</summary>
    Bridge,
}

/// <param name="Address">Address the last packet came from; the most reliable way to reach the peer.</param>
/// <param name="DiscoveryPort">UDP port the peer's packets come from; 0 when not heard directly.</param>
/// <param name="HostedGroups">Groups the peer hosts and announces; null when none.</param>
public sealed record PeerInfo(
    DeviceId DeviceId,
    string DisplayName,
    string HostName,
    string Os,
    string AppVersion,
    bool IsBusy,
    IPAddress Address,
    int ApiPort,
    PeerState State,
    DateTimeOffset LastSeen,
    PeerSource Source = PeerSource.Direct,
    int DiscoveryPort = 0,
    bool IsBridge = false,
    IReadOnlyList<PresenceGroup>? HostedGroups = null);

public enum PeerChangeKind
{
    Added,
    Updated,
    Removed,
}

public sealed record PeerChange(PeerChangeKind Kind, PeerInfo Peer);

/// <summary>
/// Merged view of peers currently visible on the network (docs/03-discovery-presence.md §10).
/// Raises <see cref="Changed"/> only when something visible changes, not on every heartbeat.
/// </summary>
public sealed class PeerDirectory(TimeProvider timeProvider)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(16);
    public static readonly TimeSpan RemoveAfter = TimeSpan.FromSeconds(30);

    /// <summary>Bridges refresh their peers about every 25 seconds (one long-poll), not every 5.</summary>
    public static readonly TimeSpan RelayedStaleAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RelayedRemoveAfter = TimeSpan.FromSeconds(90);

    private readonly Lock gate = new();
    private readonly Dictionary<DeviceId, PeerInfo> peers = [];

    public event EventHandler<PeerChange>? Changed;

    public IReadOnlyList<PeerInfo> Snapshot()
    {
        lock (gate)
        {
            return [.. peers.Values];
        }
    }

    /// <summary>Records a presence packet. <paramref name="packet"/> must already be validated by <see cref="PresenceCodec"/>.</summary>
    /// <param name="discoveryPort">UDP source port of the packet, used to probe the peer back; 0 if not from UDP.</param>
    public void Observe(PresencePacket packet, IPAddress source, int discoveryPort = 0)
    {
        PeerInfo observed = ToInfo(packet, source, PeerSource.Direct, discoveryPort);
        PeerChange? change;
        lock (gate)
        {
            change = Store(observed);
        }

        Raise(change);
    }

    /// <summary>
    /// Records presence relayed by a Bridge. The peer's own packets win while they keep coming,
    /// so a peer heard directly does not flip back and forth between the two sources.
    /// </summary>
    public void ObserveRelayed(PresencePacket packet, IPAddress address)
    {
        PeerInfo observed = ToInfo(packet, address, PeerSource.Bridge, 0);
        PeerChange? change = null;
        lock (gate)
        {
            if (!peers.TryGetValue(observed.DeviceId, out PeerInfo? existing)
                || existing.Source == PeerSource.Bridge
                || existing.State != PeerState.Online)
            {
                change = Store(observed);
            }
        }

        Raise(change);
    }

    /// <summary>The Bridge dropped the peer; a peer we also hear directly stays.</summary>
    public void RemoveRelayed(DeviceId id)
    {
        PeerInfo? removed = null;
        lock (gate)
        {
            if (peers.TryGetValue(id, out PeerInfo? existing) && existing.Source == PeerSource.Bridge)
            {
                peers.Remove(id);
                removed = existing;
            }
        }

        Raise(removed is null ? null : new PeerChange(PeerChangeKind.Removed, removed));
    }

    /// <summary>The peer said goodbye (app closed or switched to hidden).</summary>
    public void Remove(DeviceId id)
    {
        PeerInfo? removed;
        lock (gate)
        {
            peers.Remove(id, out removed);
        }

        Raise(removed is null ? null : new PeerChange(PeerChangeKind.Removed, removed));
    }

    /// <summary>Marks silent peers stale and drops those silent for too long. Call about once a second.</summary>
    public void Sweep()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var changes = new List<PeerChange>();
        lock (gate)
        {
            foreach (PeerInfo peer in peers.Values.ToList())
            {
                TimeSpan silence = now - peer.LastSeen;
                bool relayed = peer.Source == PeerSource.Bridge;
                if (silence >= (relayed ? RelayedRemoveAfter : RemoveAfter))
                {
                    peers.Remove(peer.DeviceId);
                    changes.Add(new PeerChange(PeerChangeKind.Removed, peer));
                }
                else if (silence >= (relayed ? RelayedStaleAfter : StaleAfter) && peer.State == PeerState.Online)
                {
                    PeerInfo stale = peer with { State = PeerState.Stale };
                    peers[peer.DeviceId] = stale;
                    changes.Add(new PeerChange(PeerChangeKind.Updated, stale));
                }
            }
        }

        changes.ForEach(Raise);
    }

    private PeerInfo ToInfo(PresencePacket packet, IPAddress address, PeerSource source, int discoveryPort) => new(
        DeviceId.Parse(packet.Id),
        packet.Name.Length > 0 ? packet.Name : packet.Host,
        packet.Host,
        packet.Os,
        packet.App,
        packet.Status == PresenceStatus.Busy,
        address,
        packet.Port,
        PeerState.Online,
        timeProvider.GetUtcNow(),
        source,
        discoveryPort,
        packet.Capabilities.Contains(PresenceCapabilities.Bridge),
        packet.Groups.Count > 0 ? packet.Groups : null);

    /// <summary>Stores the observation; returns the change to raise outside the lock, or null if nothing visible changed.</summary>
    private PeerChange? Store(PeerInfo observed)
    {
        PeerChange? change = !peers.TryGetValue(observed.DeviceId, out PeerInfo? existing)
            ? new PeerChange(PeerChangeKind.Added, observed)
            : !SameExceptLastSeen(existing, observed)
                ? new PeerChange(PeerChangeKind.Updated, observed)
                : null;
        peers[observed.DeviceId] = observed;
        return change;
    }

    /// <summary>Records compare lists by reference, and every packet decodes a new list of groups.</summary>
    private static bool SameExceptLastSeen(PeerInfo a, PeerInfo b) =>
        a with { LastSeen = b.LastSeen, HostedGroups = b.HostedGroups } == b
        && (a.HostedGroups ?? []).SequenceEqual(b.HostedGroups ?? []);

    private void Raise(PeerChange? change)
    {
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
    }
}
