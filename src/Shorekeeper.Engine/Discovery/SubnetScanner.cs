using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Settings;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Discovery;

/// <summary>
/// Subnet Probe (docs/03-discovery-presence.md §8): probes every address of the configured IPv4 ranges at start,
/// every <see cref="ScanEvery"/> and when the ranges change. Peers that answer stay visible through the usual
/// probes; the rate limit lives in <see cref="MulticastDiscovery"/>.
/// </summary>
public sealed class SubnetScanner(ProbeHints hints, SettingsService settings, ILogger<SubnetScanner> logger) : BackgroundService
{
    public static readonly TimeSpan ScanEvery = TimeSpan.FromMinutes(10);

    /// <summary>At most /22 (1,024 addresses) per range.</summary>
    public const int MinPrefixLength = 22;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rescan = new SemaphoreSlim(0);
        IReadOnlyList<string> scanned = [];
        settings.Changed += OnSettingsChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                scanned = settings.Current.ProbeSubnets;
                Scan(scanned, settings.Current.DiscoveryPort);
                await rescan.WaitAsync(ScanEvery, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            settings.Changed -= OnSettingsChanged;
            rescan.Dispose();
        }

        void OnSettingsChanged(object? sender, EffectiveSettings current)
        {
            if (!current.ProbeSubnets.SequenceEqual(scanned))
            {
                rescan.Release();
            }
        }
    }

    /// <summary>
    /// Parses "10.1.5.0/24" (host bits are ignored, so "10.1.5.7/24" works too) or a single address.
    /// Returns null and a user-facing reason when the text is not a usable range.
    /// </summary>
    public static (uint First, int PrefixLength)? ParseRange(string text, out string? error)
    {
        error = null;
        string[] parts = text.Trim().Split('/');
        int prefix = 32;
        if (parts.Length > 2
            || !IPAddress.TryParse(parts[0], out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)))
        {
            error = $"\"{text}\" không phải dải IPv4 (vd 10.1.5.0/24).";
            return null;
        }

        if (prefix is < MinPrefixLength or > 32)
        {
            error = $"\"{text}\" quá rộng: tối đa /{MinPrefixLength} (1.024 địa chỉ) mỗi dải.";
            return null;
        }

        byte[] bytes = address.GetAddressBytes();
        uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        uint mask = uint.MaxValue << (32 - prefix);
        return (value & mask, prefix);
    }

    /// <summary>Host addresses of a range; network and broadcast addresses are skipped up to /30.</summary>
    public static IEnumerable<IPAddress> Expand(uint first, int prefixLength)
    {
        long count = 1L << (32 - prefixLength);
        long start = prefixLength <= 30 ? 1 : 0;
        long end = prefixLength <= 30 ? count - 1 : count;
        for (long i = start; i < end; i++)
        {
            uint value = first + (uint)i;
            yield return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        }
    }

    private void Scan(IReadOnlyList<string> ranges, int port)
    {
        var targets = new List<IPEndPoint>();
        foreach (string range in ranges)
        {
            if (ParseRange(range, out string? error) is { } parsed)
            {
                targets.AddRange(Expand(parsed.First, parsed.PrefixLength).Select(a => new IPEndPoint(a, port)));
            }
            else
            {
                logger.LogWarning("Skipping probe range: {Reason}", error);
            }
        }

        if (targets.Count > 0)
        {
            logger.LogInformation("Probing {Count} addresses in {Ranges}", targets.Count, string.Join(", ", ranges));
        }

        hints.Enqueue(targets);
    }
}
