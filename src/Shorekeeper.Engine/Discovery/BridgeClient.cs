using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Discovery;

/// <param name="Address">"ip:port", or the configured text when it does not answer.</param>
/// <param name="Error">Why the last attempt failed; null while connecting or connected.</param>
public sealed record BridgeStatus(string Name, string Address, bool IsConnected, int PeerCount, string? Error);

/// <summary>
/// Client side of the Bridge role (docs/03-discovery-presence.md §7): finds Bridges (configured addresses,
/// <c>caps: bridge</c> in presence, PEX), registers our presence with up to <see cref="MaxBridges"/> of them and
/// long-polls their registries into the <see cref="PeerDirectory"/>. When this device is a Bridge itself, its own
/// registry is read the same way, in process.
/// </summary>
public sealed class BridgeClient(
    PeerClient client,
    PeerDirectory directory,
    BridgeRegistry registry,
    ILocalPresence presence,
    LocalDevice device,
    SettingsService settings,
    TimeProvider timeProvider,
    ILogger<BridgeClient> logger) : BackgroundService
{
    public const int MaxBridges = 3;
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LearnedFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResolveEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock gate = new();
    private readonly Dictionary<DeviceId, Session> sessions = [];
    private readonly Dictionary<DeviceId, (IPEndPoint Endpoint, DateTimeOffset Until)> learned = [];
    private readonly Dictionary<DeviceId, BridgeStatus> failed = [];
    private readonly Dictionary<DeviceId, DateTimeOffset> retryAt = [];
    private readonly Dictionary<string, (Candidate[] Bridges, DateTimeOffset ResolvedAt)> configured = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unreachable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A Bridge another peer told us about (PEX).</summary>
    public void Learn(DeviceId id, IPEndPoint endpoint)
    {
        if (id == device.DeviceId)
        {
            return;
        }

        lock (gate)
        {
            learned[id] = (endpoint, timeProvider.GetUtcNow() + LearnedFor);
        }
    }

    /// <summary>Bridges we are connected to or see announcing themselves; shared with contacts through PEX.</summary>
    public IReadOnlyList<KnownBridge> GetKnownBridges()
    {
        var known = new Dictionary<DeviceId, IPEndPoint>();
        foreach (PeerInfo peer in directory.Snapshot().Where(p => p is { IsBridge: true, ApiPort: > 0 }))
        {
            known[peer.DeviceId] = new IPEndPoint(peer.Address, peer.ApiPort);
        }

        lock (gate)
        {
            foreach (Session session in sessions.Values.Where(s => s.IsRemote && s.IsConnected))
            {
                known[session.Id] = session.Endpoint;
            }
        }

        return [.. known.Select(k => new KnownBridge(k.Key.Value, k.Value.Address.ToString(), k.Value.Port))];
    }

    /// <summary>For "Cài đặt → Mạng".</summary>
    public IReadOnlyList<BridgeStatus> GetStatus()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (gate)
        {
            return
            [
                .. sessions.Values.Where(s => s.IsRemote).Select(s => s.ToStatus()),
                .. failed.Where(f => !sessions.ContainsKey(f.Key) && retryAt.GetValueOrDefault(f.Key) > now - TimeSpan.FromMinutes(5)).Select(f => f.Value),
                .. unreachable.Where(a => settings.Current.BridgeAddresses.Contains(a, StringComparer.OrdinalIgnoreCase))
                    .Select(a => new BridgeStatus(a, a, false, 0, "Không tìm thấy máy hoặc máy đó không chạy Shorekeeper")),
            ];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        presence.Changed += OnPresenceChanged;
        var running = new List<Task>();
        try
        {
            using var timer = new PeriodicTimer(Tick);
            do
            {
                running.RemoveAll(t => t.IsCompleted);
                if (registry.IsEnabled && TryStart(new Session(device.DeviceId, "", new IPEndPoint(IPAddress.Loopback, 0), isRemote: false)) is { } local)
                {
                    running.Add(RunLocalAsync(local, stoppingToken));
                }

                foreach (Candidate candidate in await GetCandidatesAsync(stoppingToken))
                {
                    if (RemoteCount() >= MaxBridges)
                    {
                        break;
                    }

                    if (TryStart(new Session(candidate.Id, candidate.Name, candidate.Endpoint, isRemote: true)) is { } session)
                    {
                        running.Add(RunRemoteAsync(session, stoppingToken));
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            presence.Changed -= OnPresenceChanged;
            await Task.WhenAll(running);
        }
    }

    private int RemoteCount()
    {
        lock (gate)
        {
            return sessions.Values.Count(s => s.IsRemote);
        }
    }

    /// <summary>Registers the session unless one for that Bridge is running or it failed recently.</summary>
    private Session? TryStart(Session session)
    {
        lock (gate)
        {
            if (sessions.ContainsKey(session.Id) || retryAt.GetValueOrDefault(session.Id) > timeProvider.GetUtcNow())
            {
                return null;
            }

            sessions[session.Id] = session;
            return session;
        }
    }

    /// <summary>Configured addresses first (IT chose them), then Bridges announcing themselves, then ones learned from contacts.</summary>
    private async Task<IReadOnlyList<Candidate>> GetCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        foreach (string address in settings.Current.BridgeAddresses)
        {
            candidates.AddRange(await ResolveAsync(address, cancellationToken));
        }

        candidates.AddRange(directory.Snapshot()
            .Where(p => p is { IsBridge: true, ApiPort: > 0 })
            .OrderBy(p => p.Source)
            .Select(p => new Candidate(p.DeviceId, p.DisplayName, new IPEndPoint(p.Address, p.ApiPort))));

        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (gate)
        {
            foreach (DeviceId expired in learned.Where(l => l.Value.Until <= now).Select(l => l.Key).ToList())
            {
                learned.Remove(expired);
            }

            candidates.AddRange(learned.Select(l => new Candidate(l.Key, l.Value.Endpoint.Address.ToString(), l.Value.Endpoint)));
        }

        return [.. candidates.Where(c => c.Id != device.DeviceId).DistinctBy(c => c.Id)];
    }

    /// <summary>A configured "host" or "host:port": who answers there? Cached, including failures.</summary>
    private async Task<Candidate[]> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (configured.TryGetValue(address, out var cached) && now - cached.ResolvedAt < ResolveEvery)
        {
            return cached.Bridges;
        }

        var found = new List<Candidate>();
        ManualTarget target = ManualTarget.Parse(address);
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(target.Host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(target.Host, AddressFamily.InterNetwork, cancellationToken);
            foreach (IPAddress ip in addresses)
            {
                var endpoint = new IPEndPoint(ip, target.Port ?? settings.Current.ApiPort);
                try
                {
                    (HelloResponse hello, DeviceId id) = await client.HelloAsync(endpoint, cancellationToken);
                    found.Add(new Candidate(id, hello.Name, endpoint));
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PeerApiException)
                {
                    logger.LogDebug(ex, "Bridge {Address} did not answer at {Endpoint}", address, endpoint);
                }
            }
        }
        catch (SocketException ex)
        {
            logger.LogDebug(ex, "Cannot resolve Bridge {Address}", address);
        }

        if (found.Count == 0)
        {
            logger.LogInformation("Configured Bridge {Address} is not reachable", address);
        }

        lock (gate)
        {
            if (found.Count == 0)
            {
                unreachable.Add(address);
            }
            else
            {
                unreachable.Remove(address);
            }
        }

        configured[address] = ([.. found], now);
        return [.. found];
    }

    private async Task RunRemoteAsync(Session session, CancellationToken stoppingToken)
    {
        await Task.Yield();
        bool registered = false;
        try
        {
            (HelloResponse hello, DeviceId id) = await client.HelloAsync(session.Endpoint, stoppingToken);
            if (id != session.Id)
            {
                throw new PeerApiException(200, ApiErrorCodes.InvalidRequest);
            }

            session.Name = hello.Name;
            // The Bridge's own machine is a peer too; its registry only lists the others.
            var bridgePresence = new PresencePacket
            {
                Type = PresencePacketTypes.Reply,
                Id = hello.DeviceId,
                Name = hello.Name,
                Host = hello.Host,
                Os = hello.Os,
                App = hello.App,
                Port = hello.Port,
                Capabilities = [PresenceCapabilities.Bridge],
            };

            long since = 0;
            DateTimeOffset nextRenew = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (session.TakeRenewRequest() || timeProvider.GetUtcNow() >= nextRenew)
                {
                    registered = await RenewAsync(session, registered, stoppingToken);
                    nextRenew = timeProvider.GetUtcNow() + RenewEvery;
                }

                TimeSpan wait = Clamp(nextRenew - timeProvider.GetUtcNow(), TimeSpan.FromSeconds(1), BridgeRegistry.MaxWait);
                using CancellationTokenSource poll = session.BeginPoll(stoppingToken);
                BridgePeersResponse response;
                try
                {
                    using HttpResponseMessage http = await client.SendAsync(
                        session.Id,
                        session.Endpoint,
                        b => new HttpRequestMessage(HttpMethod.Get, new Uri(b, string.Create(CultureInfo.InvariantCulture, $"bridge/peers?since={since}&wait={(int)wait.TotalSeconds}"))),
                        wait + RequestTimeout,
                        poll.Token);
                    response = await PeerClient.ReadAsync<BridgePeersResponse>(http, poll.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && session.HasRenewRequest)
                {
                    continue; // our presence changed: tell the Bridge now rather than after the poll
                }

                since = response.Version;
                Apply(session, response);
                directory.ObserveRelayed(bridgePresence, session.Endpoint.Address);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is PeerUnreachableException or PeerApiException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            logger.LogInformation(ex, "Bridge {Bridge} at {Endpoint} is not available", session.Name, session.Endpoint);
            Fail(session, ex is PeerApiException { Status: 404 } ? "Máy này không còn làm cầu nối" : "Không kết nối được");
        }
        finally
        {
            End(session);
            if (registered && stoppingToken.IsCancellationRequested)
            {
                await UnregisterAsync(session);
            }
        }
    }

    /// <summary>Registers (or renews) our presence; a hidden device unregisters instead. Returns whether we are registered.</summary>
    private async Task<bool> RenewAsync(Session session, bool registered, CancellationToken cancellationToken)
    {
        if (presence.Status == MyStatus.Hidden)
        {
            if (registered)
            {
                using HttpResponseMessage gone = await client.SendAsync(
                    session.Id, session.Endpoint, b => new HttpRequestMessage(HttpMethod.Delete, new Uri(b, "bridge/register")), RequestTimeout, cancellationToken);
                await PeerClient.EnsureSuccessAsync(gone, cancellationToken);
            }

            return false;
        }

        PresencePacket me = presence.CreatePacket(PresencePacketTypes.Announce);
        var body = new BridgeRegistration(me.Name, me.Host, me.Os, me.App, me.Status, me.Port);
        using HttpResponseMessage response = await client.SendAsync(
            session.Id,
            session.Endpoint,
            b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, "bridge/register")) { Content = JsonContent.Create(body) },
            RequestTimeout,
            cancellationToken);
        await PeerClient.ReadAsync<BridgeRegistered>(response, cancellationToken);
        return true;
    }

    private async Task UnregisterAsync(Session session)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using HttpResponseMessage response = await client.SendAsync(
                session.Id, session.Endpoint, b => new HttpRequestMessage(HttpMethod.Delete, new Uri(b, "bridge/register")), TimeSpan.FromSeconds(2), timeout.Token);
        }
        catch (Exception ex) when (ex is PeerUnreachableException or HttpRequestException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not unregister from Bridge {Bridge}", session.Name);
        }
    }

    /// <summary>This device is a Bridge: show the peers registered with us.</summary>
    private async Task RunLocalAsync(Session session, CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            long since = 0;
            while (await registry.GetPeersAsync(since, BridgeRegistry.MaxWait, stoppingToken) is { } response)
            {
                since = response.Version;
                Apply(session, response);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            End(session);
        }
    }

    /// <summary>
    /// Merges a registry response into the session's view and the directory. Every peer is observed again on each
    /// response (at least every 25 seconds), which keeps relayed peers from going stale.
    /// </summary>
    private void Apply(Session session, BridgePeersResponse response)
    {
        var gone = new List<DeviceId>();
        BridgePeer[] current;
        lock (gate)
        {
            if (response.Full)
            {
                gone.AddRange(session.Peers.Keys);
                session.Peers.Clear();
            }

            foreach (BridgePeer peer in response.Peers)
            {
                if (DeviceId.TryParse(peer.DeviceId, out DeviceId id) && id != device.DeviceId && IPAddress.TryParse(peer.Address, out _))
                {
                    session.Peers[id] = peer;
                }
            }

            foreach (string removed in response.Removed)
            {
                if (DeviceId.TryParse(removed, out DeviceId id) && session.Peers.Remove(id))
                {
                    gone.Add(id);
                }
            }

            // Still listed by this or another Bridge: not gone.
            gone.RemoveAll(id => sessions.Values.Any(s => s.Peers.ContainsKey(id)));
            session.IsConnected = true;
            failed.Remove(session.Id);
            current = [.. session.Peers.Values];
        }

        foreach (DeviceId id in gone)
        {
            directory.RemoveRelayed(id);
        }

        foreach (BridgePeer peer in current)
        {
            directory.ObserveRelayed(ToPacket(peer), IPAddress.Parse(peer.Address));
        }
    }

    private static PresencePacket ToPacket(BridgePeer peer) => new()
    {
        Type = PresencePacketTypes.Reply,
        Id = peer.DeviceId,
        Name = peer.Name.Length > PresenceCodec.MaxNameLength ? peer.Name[..PresenceCodec.MaxNameLength] : peer.Name,
        Host = peer.Host,
        Os = peer.Os,
        App = peer.App,
        Port = peer.Port,
        Status = peer.Status,
    };

    private void Fail(Session session, string error)
    {
        lock (gate)
        {
            retryAt[session.Id] = timeProvider.GetUtcNow() + RetryAfter;
            failed[session.Id] = session.ToStatus() with { IsConnected = false, Error = error };
        }
    }

    /// <summary>Peers only this session listed expire on their own (90 seconds) unless another Bridge lists them.</summary>
    private void End(Session session)
    {
        lock (gate)
        {
            sessions.Remove(session.Id);
        }
    }

    private void OnPresenceChanged(object? sender, EventArgs e)
    {
        lock (gate)
        {
            foreach (Session session in sessions.Values)
            {
                session.RequestRenew();
            }
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    private sealed record Candidate(DeviceId Id, string Name, IPEndPoint Endpoint);

    private sealed class Session(DeviceId id, string name, IPEndPoint endpoint, bool isRemote)
    {
        private readonly Lock pollGate = new();
        private CancellationTokenSource? poll;
        private volatile bool renewRequested;

        public DeviceId Id { get; } = id;

        public string Name { get; set; } = name;

        public IPEndPoint Endpoint { get; } = endpoint;

        public bool IsRemote { get; } = isRemote;

        /// <summary>Guarded by the client's lock, like <see cref="Peers"/>.</summary>
        public bool IsConnected { get; set; }

        /// <summary>Guarded by the client's lock.</summary>
        public Dictionary<DeviceId, BridgePeer> Peers { get; } = [];

        public bool HasRenewRequest => renewRequested;

        public CancellationTokenSource BeginPoll(CancellationToken stoppingToken)
        {
            lock (pollGate)
            {
                poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                if (renewRequested)
                {
                    poll.Cancel();
                }

                return poll;
            }
        }

        public void RequestRenew()
        {
            lock (pollGate)
            {
                renewRequested = true;
                try
                {
                    poll?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // That poll already finished.
                }
            }
        }

        public bool TakeRenewRequest()
        {
            bool requested = renewRequested;
            renewRequested = false;
            return requested;
        }

        public BridgeStatus ToStatus() => new(Name, Endpoint.ToString(), IsConnected, Peers.Count, null);
    }
}
