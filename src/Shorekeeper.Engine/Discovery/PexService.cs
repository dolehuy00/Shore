using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Discovery;

/// <summary>
/// Peer Exchange (docs/03-discovery-presence.md §5): contacts tell each other which peers they hear directly.
/// Learned peers are only probed; they appear once they answer, so nothing unverified is shown.
/// One connection with a machine in another subnet is enough for both subnets to start seeing each other.
/// </summary>
public sealed class PexService(
    PeerClient client,
    PeerDirectory directory,
    TrustStore trust,
    ProbeHints hints,
    BridgeClient bridges,
    LocalDevice device,
    TimeProvider timeProvider,
    ILogger<PexService> logger) : BackgroundService
{
    public static readonly TimeSpan ExchangeEvery = TimeSpan.FromMinutes(5);
    public const int MaxPeers = 500;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int ContactsPerTick = 3;

    /// <summary>Probe a learned peer for this long; the next exchange renews it.</summary>
    private static readonly TimeSpan ProbeFor = TimeSpan.FromMinutes(1);

    private readonly Dictionary<DeviceId, DateTimeOffset> lastExchange = [];

    /// <summary>Server side of <c>GET /peers/known</c>: peers we hear ourselves, never ones only a Bridge told us about.</summary>
    public KnownPeersResponse GetKnown(DeviceId caller) => new(
        [
            .. directory.Snapshot()
                .Where(p => p is { Source: PeerSource.Direct, State: PeerState.Online, DiscoveryPort: > 0 } && p.DeviceId != caller)
                .Take(MaxPeers)
                .Select(p => new KnownPeer(p.DeviceId.Value, p.DisplayName, p.Address.ToString(), p.DiscoveryPort)),
        ],
        [.. bridges.GetKnownBridges().Where(b => b.DeviceId != caller.Value)]);

    /// <summary>Asks one contact for its peers. Returns how many new peers will be probed.</summary>
    public async Task<int> ExchangeAsync(DeviceId contact, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.SendAsync(
            contact, b => new HttpRequestMessage(HttpMethod.Get, new Uri(b, "peers/known")), RequestTimeout, cancellationToken);
        KnownPeersResponse known = await PeerClient.ReadAsync<KnownPeersResponse>(response, cancellationToken);

        HashSet<DeviceId> heard = [.. directory.Snapshot().Where(p => p is { Source: PeerSource.Direct, State: PeerState.Online }).Select(p => p.DeviceId)];
        int learned = 0;
        foreach (KnownPeer peer in known.Peers.Take(MaxPeers))
        {
            if (DeviceId.TryParse(peer.DeviceId, out DeviceId id)
                && id != device.DeviceId
                && !heard.Contains(id)
                && trust.GetLevel(id) != TrustLevel.Blocked
                && IPAddress.TryParse(peer.Address, out IPAddress? address)
                && peer.DiscoveryPort is > 0 and <= 65535)
            {
                hints.Add(new IPEndPoint(address, peer.DiscoveryPort), ProbeFor);
                learned++;
            }
        }

        foreach (KnownBridge bridge in known.Bridges.Take(10))
        {
            if (DeviceId.TryParse(bridge.DeviceId, out DeviceId id) && IPAddress.TryParse(bridge.Address, out IPAddress? address) && bridge.Port is > 0 and <= 65535)
            {
                bridges.Learn(id, new IPEndPoint(address, bridge.Port));
            }
        }

        return learned;
    }

    /// <summary>Contacts online right now are asked when they appear, then every <see cref="ExchangeEvery"/>.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Tick);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                DateTimeOffset now = timeProvider.GetUtcNow();
                PeerInfo[] due =
                [
                    .. directory.Snapshot()
                        .Where(p => p.State == PeerState.Online && trust.GetLevel(p.DeviceId) == TrustLevel.Trusted)
                        .Where(p => !lastExchange.TryGetValue(p.DeviceId, out DateTimeOffset last) || now - last >= ExchangeEvery)
                        .Take(ContactsPerTick),
                ];

                foreach (PeerInfo contact in due)
                {
                    lastExchange[contact.DeviceId] = now;
                    try
                    {
                        int learned = await ExchangeAsync(contact.DeviceId, stoppingToken);
                        logger.LogDebug("PEX with {Peer}: {Count} new peers to probe", contact.DisplayName, learned);
                    }
                    catch (Exception ex) when (ex is PeerUnreachableException or PeerApiException or HttpRequestException or System.Text.Json.JsonException)
                    {
                        logger.LogDebug(ex, "PEX with {Peer} failed", contact.DisplayName);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
