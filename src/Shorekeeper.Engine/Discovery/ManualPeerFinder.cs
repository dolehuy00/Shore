using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Discovery;

public sealed record FoundPeer(DeviceId DeviceId, string Name, string Host, IPEndPoint Endpoint);

/// <param name="Error">User-facing reason when <paramref name="Peer"/> is null.</param>
public sealed record FindResult(FoundPeer? Peer, string? Error);

/// <summary>"Thêm máy khác": reach a machine by host name or IP, e.g. in another subnet (docs/03-discovery-presence.md §9).</summary>
public sealed class ManualPeerFinder(
    PeerClient client,
    PeerDirectory directory,
    TrustStore trust,
    LocalDevice device,
    SettingsService settings,
    ILogger<ManualPeerFinder> logger)
{
    public async Task<FindResult> FindAsync(string input, CancellationToken cancellationToken)
    {
        ManualTarget target = ManualTarget.Parse(input);
        if (target.Host.Length == 0)
        {
            return new FindResult(null, "Hãy nhập tên máy hoặc địa chỉ IP.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(target.Host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(target.Host, AddressFamily.InterNetwork, cancellationToken);
        }
        catch (SocketException)
        {
            return new FindResult(null, $"Không tìm thấy máy tên \"{target.Host}\" trong mạng.");
        }

        int port = target.Port ?? settings.Current.ApiPort;
        foreach (IPAddress address in addresses)
        {
            var endpoint = new IPEndPoint(address, port);
            try
            {
                (HelloResponse hello, DeviceId id) = await client.HelloAsync(endpoint, cancellationToken);
                if (id == device.DeviceId)
                {
                    return new FindResult(null, "Đó là máy của bạn.");
                }

                // Show it in "Xóm" right away; probes keep it there if the user saves it.
                directory.Observe(
                    new PresencePacket { Type = PresencePacketTypes.Reply, Id = id.Value, Name = hello.Name, Host = hello.Host, Os = hello.Os, App = hello.App, Port = hello.Port },
                    address);
                return new FindResult(new FoundPeer(id, hello.Name, hello.Host, endpoint), null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PeerApiException)
            {
                logger.LogInformation(ex, "No Shorekeeper answer from {Endpoint}", endpoint);
            }
        }

        return new FindResult(null,
            $"Không kết nối được tới {target.Host}. Máy đó có thể chưa mở Shorekeeper, sai cổng, hoặc firewall đang chặn.");
    }

    /// <summary>Remember the machine so it is probed and found again after a restart.</summary>
    public Task SaveAsync(string input, CancellationToken cancellationToken = default) =>
        trust.AddManualTargetAsync(input, cancellationToken);
}
