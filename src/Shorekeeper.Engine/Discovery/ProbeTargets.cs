using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Discovery;

/// <summary>Addresses to probe by unicast, so peers outside the multicast reach stay visible.</summary>
public interface IProbeTargetSource
{
    Task<IReadOnlyList<IPEndPoint>> GetTargetsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Contacts' last known addresses plus machines the user added by host name or IP
/// (docs/03-discovery-presence.md §4, §7). All peers use the same UDP discovery port.
/// </summary>
public sealed class ProbeTargets(TrustStore trust, SettingsService settings, TimeProvider timeProvider, ILogger<ProbeTargets> logger)
    : IProbeTargetSource
{
    private const int MaxTargets = 200;
    private static readonly TimeSpan ResolveEvery = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, (IPAddress[] Addresses, DateTimeOffset ResolvedAt)> resolved = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<IPEndPoint>> GetTargetsAsync(CancellationToken cancellationToken)
    {
        var addresses = new List<IPAddress>(await trust.GetContactAddressesAsync(cancellationToken));
        foreach (string target in trust.GetManualTargets())
        {
            addresses.AddRange(await ResolveAsync(ManualTarget.Parse(target).Host, cancellationToken));
        }

        int port = settings.Current.DiscoveryPort;
        return [.. addresses.Distinct().Take(MaxTargets).Select(a => new IPEndPoint(a, port))];
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return [literal];
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (resolved.TryGetValue(host, out var cached) && now - cached.ResolvedAt < ResolveEvery)
        {
            return cached.Addresses;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
        }
        catch (SocketException ex)
        {
            logger.LogDebug(ex, "Cannot resolve {Host}", host);
            addresses = [];
        }

        // Failures are cached too, so an offline name is not looked up every few seconds.
        resolved[host] = (addresses, now);
        return addresses;
    }
}

/// <summary>"host", "host:port", "10.1.5.12" or "10.1.5.12:47471" as typed by the user. The port is the HTTPS API port.</summary>
public sealed record ManualTarget(string Host, int? Port)
{
    public static ManualTarget Parse(string text)
    {
        text = text.Trim();
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int port) && port is > 0 and <= 65535)
        {
            return new ManualTarget(text[..colon], port);
        }

        return new ManualTarget(text, null);
    }
}
