using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Discovery;

/// <summary>
/// Same-subnet discovery over UDP multicast (docs/03-discovery-presence.md §3).
/// One socket bound to the discovery port joins the group on every usable interface;
/// multicast sends go out on each interface in turn. The socket is rebuilt when the network changes.
/// </summary>
public sealed class MulticastDiscovery : BackgroundService
{
    public static readonly IPAddress Group = IPAddress.Parse("239.255.47.47");
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly ILocalPresence presence;
    private readonly PeerDirectory directory;
    private readonly ILogger<MulticastDiscovery> logger;
    private readonly Func<int> getPort;
    private readonly IPAddress bindAddress;
    private readonly Func<IReadOnlyList<LocalInterface>> getInterfaces;

    private int port;
    private volatile bool networkChanged;
    private volatile bool presenceChanged;

    public MulticastDiscovery(ILocalPresence presence, PeerDirectory directory, SettingsService settings, ILogger<MulticastDiscovery> logger)
        : this(presence, directory, logger, () => settings.Current.DiscoveryPort, IPAddress.Any, LocalInterfaces.GetUsable)
    {
    }

    /// <summary>Tests bind to loopback on a private port so nothing reaches the real network.</summary>
    internal MulticastDiscovery(
        ILocalPresence presence,
        PeerDirectory directory,
        ILogger<MulticastDiscovery> logger,
        Func<int> getPort,
        IPAddress bindAddress,
        Func<IReadOnlyList<LocalInterface>> getInterfaces)
    {
        this.presence = presence;
        this.directory = directory;
        this.logger = logger;
        this.getPort = getPort;
        this.bindAddress = bindAddress;
        this.getInterfaces = getInterfaces;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read when running, not when constructed: settings are loaded by the engine's own start-up.
        port = getPort();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        presence.Changed += OnPresenceChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                networkChanged = false;
                try
                {
                    await RunSessionAsync(stoppingToken);
                }
                catch (SocketException ex)
                {
                    // E.g. another program holds the port exclusively. Keep the app running and retry.
                    logger.LogError(ex, "Discovery cannot use UDP port {Port}; retrying in {Delay}", port, RetryDelay);
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            presence.Changed -= OnPresenceChanged;
        }
    }

    /// <summary>Runs until the network changes (then the caller rebuilds the socket) or the service stops.</summary>
    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<LocalInterface> interfaces = getInterfaces();
        using Socket socket = OpenSocket(interfaces);
        string[] addresses = [.. interfaces.Select(i => i.Address.ToString())];
        logger.LogInformation("Discovery listening on port {Port}, interfaces: {Interfaces}", port, string.Join(", ", addresses));
        if (interfaces.Count == 0)
        {
            logger.LogWarning("No usable network interface; waiting for a network change");
        }

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task receiving = ReceiveLoopAsync(socket, addresses, session.Token);

        bool announced = false;
        DateTimeOffset nextHeartbeat = DateTimeOffset.MinValue;
        try
        {
            using var timer = new PeriodicTimer(Tick);
            do
            {
                directory.Sweep();
                bool hidden = presence.Status == MyStatus.Hidden;

                if (presenceChanged || !announced)
                {
                    presenceChanged = false;
                    announced = true;
                    SendMulticast(socket, interfaces, addresses, hidden ? PresencePacketTypes.Bye : PresencePacketTypes.Announce);
                    nextHeartbeat = NextHeartbeat();
                }
                else if (!hidden && DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    SendMulticast(socket, interfaces, addresses, PresencePacketTypes.Heartbeat);
                    nextHeartbeat = NextHeartbeat();
                }
            }
            while (!networkChanged && await timer.WaitForNextTickAsync(stoppingToken));
        }
        finally
        {
            if (stoppingToken.IsCancellationRequested && presence.Status != MyStatus.Hidden)
            {
                SendMulticast(socket, interfaces, addresses, PresencePacketTypes.Bye);
            }

            await session.CancelAsync();
            await receiving;
        }

        logger.LogInformation("Network changed; restarting discovery");
    }

    private Socket OpenSocket(IReadOnlyList<LocalInterface> interfaces)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Several Shorekeeper processes (e.g. two Windows sessions) may share the port.
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (OperatingSystem.IsWindows())
            {
                // Otherwise an ICMP "port unreachable" from a unicast reply breaks the next receive.
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            socket.Bind(new IPEndPoint(bindAddress, port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            foreach (LocalInterface nic in interfaces)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, nic.Address));
                }
                catch (SocketException ex)
                {
                    logger.LogWarning(ex, "Cannot join multicast group on {Interface} ({Address})", nic.Name, nic.Address);
                }
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, string[] addresses, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[PresenceCodec.MaxPacketSize + 1];
        EndPoint anyEndPoint = new IPEndPoint(IPAddress.Any, 0);
        string myId = presence.CreatePacket(PresencePacketTypes.Heartbeat).Id;

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Discovery receive failed");
                continue;
            }

            if (!PresenceCodec.TryDecode(buffer.AsSpan(0, result.ReceivedBytes), out PresencePacket? packet) || packet.Id == myId)
            {
                continue;
            }

            var source = (IPEndPoint)result.RemoteEndPoint;
            if (packet.Type == PresencePacketTypes.Bye)
            {
                directory.Remove(DeviceId.Parse(packet.Id));
                continue;
            }

            directory.Observe(packet, source.Address);
            if (packet.Type == PresencePacketTypes.Announce)
            {
                _ = ReplyAsync(socket, source.Address, addresses, cancellationToken);
            }
        }
    }

    /// <summary>A newcomer announced itself: tell it about us right away instead of making it wait for our heartbeat.</summary>
    private async Task ReplyAsync(Socket socket, IPAddress target, string[] addresses, CancellationToken cancellationToken)
    {
        try
        {
            // Random delay so a crowded subnet does not answer all at once.
            await Task.Delay(Random.Shared.Next(0, 500), cancellationToken);
            if (presence.Status == MyStatus.Hidden)
            {
                return;
            }

            byte[] data = Encode(PresencePacketTypes.Reply, addresses);
            await socket.SendToAsync(data, SocketFlags.None, new IPEndPoint(target, port), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Discovery reply to {Target} failed", target);
        }
    }

    private void SendMulticast(Socket socket, IReadOnlyList<LocalInterface> interfaces, string[] addresses, string type)
    {
        byte[] data = Encode(type, addresses);
        var groupEndPoint = new IPEndPoint(Group, port);
        foreach (LocalInterface nic in interfaces)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, nic.Address.GetAddressBytes());
                socket.SendTo(data, groupEndPoint);
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Discovery {Type} on {Interface} failed", type, nic.Name);
            }
        }
    }

    private byte[] Encode(string type, string[] addresses) =>
        PresenceCodec.Encode(presence.CreatePacket(type) with { Addresses = addresses });

    private static DateTimeOffset NextHeartbeat() =>
        DateTimeOffset.UtcNow + HeartbeatInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => networkChanged = true;

    private void OnPresenceChanged(object? sender, EventArgs e) => presenceChanged = true;
}
