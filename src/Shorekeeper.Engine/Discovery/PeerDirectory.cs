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

/// <param name="Address">Address the last packet came from; the most reliable way to reach the peer.</param>
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
    DateTimeOffset LastSeen);

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
    public void Observe(PresencePacket packet, IPAddress source)
    {
        DeviceId id = DeviceId.Parse(packet.Id);
        var observed = new PeerInfo(
            id,
            packet.Name.Length > 0 ? packet.Name : packet.Host,
            packet.Host,
            packet.Os,
            packet.App,
            packet.Status == PresenceStatus.Busy,
            source,
            packet.Port,
            PeerState.Online,
            timeProvider.GetUtcNow());

        PeerChange? change;
        lock (gate)
        {
            change = !peers.TryGetValue(id, out PeerInfo? existing)
                ? new PeerChange(PeerChangeKind.Added, observed)
                : existing with { LastSeen = observed.LastSeen } != observed
                    ? new PeerChange(PeerChangeKind.Updated, observed)
                    : null;
            peers[id] = observed;
        }

        Raise(change);
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
                if (silence >= RemoveAfter)
                {
                    peers.Remove(peer.DeviceId);
                    changes.Add(new PeerChange(PeerChangeKind.Removed, peer));
                }
                else if (silence >= StaleAfter && peer.State == PeerState.Online)
                {
                    PeerInfo stale = peer with { State = PeerState.Stale };
                    peers[peer.DeviceId] = stale;
                    changes.Add(new PeerChange(PeerChangeKind.Updated, stale));
                }
            }
        }

        changes.ForEach(Raise);
    }

    private void Raise(PeerChange? change)
    {
        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
    }
}
