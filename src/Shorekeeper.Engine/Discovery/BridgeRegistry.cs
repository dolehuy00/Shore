using System.Net;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Discovery;

public enum BridgeRegisterResult
{
    Registered,
    Disabled,
    Invalid,
    Full,
}

/// <summary>
/// The Bridge role (docs/08-host-roles.md §4): an in-memory registry of public presence that peers in any subnet
/// register with and read, so subnets see each other without multicast. Entries are keyed by the caller's
/// certificate, so nobody can register in someone else's name. Readers long-poll for changes since a version.
/// </summary>
public sealed class BridgeRegistry
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);

    /// <summary>Changes arriving together (e.g. many PCs starting at 8:00) go out in one response.</summary>
    public static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(1);

    /// <summary>Readers that fell further behind than this get the whole registry again.</summary>
    public static readonly TimeSpan RemovalRetention = TimeSpan.FromMinutes(5);

    public const int MaxEntries = 2000;

    private readonly SettingsService settings;
    private readonly TimeProvider timeProvider;
    private readonly Lock gate = new();
    private readonly Dictionary<DeviceId, Entry> entries = [];
    private readonly List<Removal> removals = [];
    private long version;

    /// <summary>Removals up to this version were forgotten.</summary>
    private long forgottenUpTo;

    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BridgeRegistry(SettingsService settings, TimeProvider timeProvider)
    {
        this.settings = settings;
        this.timeProvider = timeProvider;
        // Versions continue from the start time, so a reader's version from before a restart counts as too old.
        version = forgottenUpTo = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    public bool IsEnabled => settings.Current.BridgeEnabled;

    public int Count
    {
        get
        {
            lock (gate)
            {
                Sweep();
                return entries.Count;
            }
        }
    }

    public (BridgeRegisterResult Result, BridgeRegistered? Response) Register(DeviceId caller, IPAddress from, BridgeRegistration body)
    {
        if (!IsEnabled)
        {
            return (BridgeRegisterResult.Disabled, null);
        }

        if (!IsValid(body))
        {
            return (BridgeRegisterResult.Invalid, null);
        }

        string address = from.MapToIPv4().ToString();
        var peer = new BridgePeer(caller.Value, body.Name.Trim(), body.Host, body.Os, body.App, body.Status, address, body.Port);
        DateTimeOffset expiresAt = timeProvider.GetUtcNow() + Lease;
        lock (gate)
        {
            Sweep();
            if (entries.TryGetValue(caller, out Entry? existing) && existing.Peer == peer)
            {
                // A renewal: nothing for readers to see.
                entries[caller] = existing with { ExpiresAt = expiresAt };
            }
            else if (existing is null && entries.Count >= MaxEntries)
            {
                return (BridgeRegisterResult.Full, null);
            }
            else
            {
                removals.RemoveAll(r => r.DeviceId == caller);
                entries[caller] = new Entry(peer, expiresAt, NextVersion());
                Signal();
            }
        }

        return (BridgeRegisterResult.Registered, new BridgeRegistered((int)Lease.TotalSeconds, address));
    }

    public void Unregister(DeviceId caller)
    {
        lock (gate)
        {
            if (entries.Remove(caller))
            {
                removals.Add(new Removal(caller, NextVersion(), timeProvider.GetUtcNow()));
                Signal();
            }
        }
    }

    /// <summary>
    /// Changes after <paramref name="since"/> (0 = everything), waiting up to <paramref name="wait"/> for one.
    /// Null when the Bridge role is off.
    /// </summary>
    public async Task<BridgePeersResponse?> GetPeersAsync(long since, TimeSpan wait, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return null;
        }

        Task signal;
        lock (gate)
        {
            Sweep();
            if (NeedsFull(since) || version > since)
            {
                return Build(since);
            }

            signal = changed.Task;
        }

        try
        {
            await signal.WaitAsync(wait, cancellationToken);
            await Task.Delay(BatchDelay, cancellationToken);
        }
        catch (TimeoutException)
        {
        }

        lock (gate)
        {
            Sweep();
            return Build(since);
        }
    }

    private static bool IsValid(BridgeRegistration body) =>
        body.Name is { Length: <= PresenceCodec.MaxNameLength }
        && body.Host is { Length: <= 255 }
        && body.Os is { Length: <= 32 }
        && body.App is { Length: <= 32 }
        && body.Status is PresenceStatus.Available or PresenceStatus.Busy
        && body.Port is > 0 and <= 65535;

    /// <summary>Since 0, a version from another run, or older than the removals we still remember.</summary>
    private bool NeedsFull(long since) => since <= 0 || since > version || since < forgottenUpTo;

    private BridgePeersResponse Build(long since) => NeedsFull(since)
        ? new BridgePeersResponse(version, true, [.. entries.Values.Select(e => e.Peer)], [])
        : new BridgePeersResponse(
            version,
            false,
            [.. entries.Values.Where(e => e.Version > since).Select(e => e.Peer)],
            [.. removals.Where(r => r.Version > since).Select(r => r.DeviceId.Value)]);

    /// <summary>Drops expired leases and old removals. Called under the lock.</summary>
    private void Sweep()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (Entry expired in entries.Values.Where(e => e.ExpiresAt <= now).ToList())
        {
            DeviceId id = DeviceId.Parse(expired.Peer.DeviceId);
            entries.Remove(id);
            removals.Add(new Removal(id, NextVersion(), now));
            Signal();
        }

        foreach (Removal old in removals.Where(r => now - r.At > RemovalRetention).ToList())
        {
            removals.Remove(old);
            forgottenUpTo = Math.Max(forgottenUpTo, old.Version);
        }
    }

    private long NextVersion() => ++version;

    private void Signal()
    {
        changed.TrySetResult();
        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Entry(BridgePeer Peer, DateTimeOffset ExpiresAt, long Version);

    private sealed record Removal(DeviceId DeviceId, long Version, DateTimeOffset At);
}
