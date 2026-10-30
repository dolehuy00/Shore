using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Engine.Trust;

/// <summary>
/// A contact that renames itself is shown by its new name here (unless we gave it an alias). Presence is only a hint
/// (unsigned UDP: anyone could announce a contact's id with another name, docs/04-identity-security.md §6), so a name
/// there that differs from the one we keep makes us ask the device itself over mutual TLS, and only its answer is kept.
/// </summary>
public sealed class ContactNames(TrustStore trust, PeerClient client, TimeProvider timeProvider, ILogger<ContactNames> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A name heard again is asked about again only after this: forged packets do not keep us asking.</summary>
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<DeviceId, (string Name, DateTimeOffset At)> asked = new();

    /// <summary>Called for each presence heard.</summary>
    public void Observe(PeerInfo peer)
    {
        if (trust.Get(peer.DeviceId) is not { } record || string.IsNullOrWhiteSpace(peer.DisplayName) || peer.DisplayName == record.DisplayName)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (asked.TryGetValue(peer.DeviceId, out var last) && last.Name == peer.DisplayName && now - last.At < AskAgainAfter)
        {
            return;
        }

        asked[peer.DeviceId] = (peer.DisplayName, now);
        _ = ConfirmAsync(peer.DeviceId);
    }

    private async Task ConfirmAsync(DeviceId id)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                id, b => new HttpRequestMessage(HttpMethod.Get, new Uri(b, "hello")), RequestTimeout, CancellationToken.None);
            HelloResponse hello = await PeerClient.ReadAsync<HelloResponse>(response, CancellationToken.None);
            string name = hello.Name?.Trim() ?? "";
            string host = hello.Host?.Trim() ?? "";
            if (hello.DeviceId == id.Value && name.Length is > 0 and <= PresenceCodec.MaxNameLength && host.Length <= PresenceCodec.MaxNameLength)
            {
                await trust.RecordNameAsync(id, name, host);
            }
        }
        catch (Exception ex) when (ex is PeerUnreachableException or PeerApiException or HttpRequestException or JsonException or OperationCanceledException)
        {
            // Not reachable now, or not the device it claimed: asked again when its name is heard after a while.
            logger.LogDebug(ex, "Could not confirm the name of {Peer}", id);
        }
    }
}
