using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Identity;

namespace Shorekeeper.Engine.Discovery;

/// <summary>
/// Pictures of other people (docs/13-active-directory.md §2), fetched from <c>GET /hello/avatar</c> when a peer
/// announces one and kept in memory per picture version.
/// </summary>
public sealed class PeerAvatars(PeerClient client, ILogger<PeerAvatars> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(DeviceId Peer, string Tag), Task<byte[]?>> cache = new();

    /// <returns>The picture, or null when the peer has none or could not send it.</returns>
    public Task<byte[]?> GetAsync(PeerInfo peer)
    {
        if (peer.AvatarTag is not { } tag)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return cache.GetOrAdd((peer.DeviceId, tag), FetchAsync);
    }

    private async Task<byte[]?> FetchAsync((DeviceId Peer, string Tag) key)
    {
        // Lets GetOrAdd store the task before a failure tries to remove it.
        await Task.Yield();
        DeviceId peer = key.Peer;
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                peer, b => new HttpRequestMessage(HttpMethod.Get, new Uri(b, "hello/avatar")), RequestTimeout, CancellationToken.None);
            await PeerClient.EnsureSuccessAsync(response, CancellationToken.None);
            byte[] photo = await response.Content.ReadAsByteArrayAsync();
            return photo.Length is > 0 and <= DirectoryProfileService.MaxPhotoBytes ? photo : null;
        }
        catch (Exception ex) when (ex is PeerUnreachableException or PeerApiException or HttpRequestException)
        {
            logger.LogDebug(ex, "Picture of {Peer} not available", peer);
            // Tried again next time.
            cache.TryRemove(key, out _);
            return null;
        }
    }
}
