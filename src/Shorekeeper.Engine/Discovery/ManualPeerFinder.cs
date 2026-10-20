using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Trust;
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
            return new FindResult(null, L.T("Hãy nhập tên máy hoặc địa chỉ IP."));
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
            return new FindResult(null, L.F("Không tìm thấy máy tên \"{0}\" trong mạng.", target.Host));
        }

        int port = target.Port ?? settings.Current.ApiPort;
        foreach (IPAddress address in addresses)
        {
            if (await HelloAsync(new IPEndPoint(address, port), cancellationToken) is { } found)
            {
                return found.DeviceId == device.DeviceId ? new FindResult(null, L.T("Đó là máy của bạn.")) : new FindResult(found, null);
            }
        }

        return new FindResult(null,
            L.F("Không kết nối được tới {0}. Máy đó có thể chưa mở Shorekeeper, sai cổng, hoặc firewall đang chặn.", target.Host));
    }

    /// <summary>
    /// "Kết nối bằng mã QR": the computer the code names, at one of its addresses, all tried at once (some, like a
    /// hotspot's, are often unreachable); whatever else answers there is not taken. Its address is remembered when it
    /// is in another subnet, which discovery does not reach.
    /// </summary>
    public async Task<FindResult> FindAsync(PairingInviteLink link, CancellationToken cancellationToken)
    {
        if (link.DeviceId == device.DeviceId)
        {
            return new FindResult(null, L.T("Đó là máy của bạn."));
        }

        using var rest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<FoundPeer?>> attempts = [.. link.Endpoints.Select(endpoint => HelloAsync(endpoint, rest.Token))];
        while (attempts.Count > 0)
        {
            Task<FoundPeer?> answered = await Task.WhenAny(attempts);
            attempts.Remove(answered);
            if (await answered is not { } found)
            {
                continue;
            }

            if (found.DeviceId != link.DeviceId)
            {
                logger.LogWarning("{Endpoint} answered as {Peer}, not the {Expected} of the QR code", found.Endpoint, found.DeviceId, link.DeviceId);
                continue;
            }

            await rest.CancelAsync();
            if (!LocalInterfaces.IsOnLink(found.Endpoint.Address, LocalInterfaces.GetUsable()))
            {
                await SaveAsync(found.Endpoint.ToString(), cancellationToken);
            }

            return new FindResult(found, null);
        }

        return new FindResult(null,
            L.T("Không liên lạc được với máy trong mã QR. Hai máy cần cùng mạng (hoặc điện thoại vào điểm phát Wi-Fi của máy kia) và máy kia đang mở Shorekeeper."));
    }

    /// <summary>Remember the machine so it is probed and found again after a restart.</summary>
    public Task SaveAsync(string input, CancellationToken cancellationToken = default) =>
        trust.AddManualTargetAsync(input, cancellationToken);

    /// <summary>Who answers at <paramref name="endpoint"/>, shown in "Xóm" right away; null when nothing does.</summary>
    private async Task<FoundPeer?> HelloAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            (HelloResponse hello, DeviceId id) = await client.HelloAsync(endpoint, cancellationToken);
            if (id != device.DeviceId)
            {
                // Probes keep it there if the user saves it.
                directory.Observe(
                    new PresencePacket { Type = PresencePacketTypes.Reply, Id = id.Value, Name = hello.Name, Host = hello.Host, Os = hello.Os, App = hello.App, Port = hello.Port },
                    endpoint.Address);
            }

            return new FoundPeer(id, hello.Name, hello.Host, endpoint);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PeerApiException)
        {
            logger.LogInformation(ex, "No Shorekeeper answer from {Endpoint}", endpoint);
            return null;
        }
    }
}
