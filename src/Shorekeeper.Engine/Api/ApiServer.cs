using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Api;

public sealed class ApiServerOptions
{
    /// <summary>Tests bind to loopback; the app listens on all interfaces.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
}

/// <summary>
/// The HTTPS endpoint other peers call (docs/05-protocol.md): mutual TLS with the device certificate, requests
/// handed to <see cref="PeerApi"/>. <see cref="MiniHttpServer"/> runs everywhere; desktops may use Kestrel instead.
/// </summary>
public abstract class ApiServer(SettingsService settings, ILogger logger) : IHostedService
{
    /// <summary>Port actually listening; 0 until started.</summary>
    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Port = await ListenAsync(settings.Current.ApiPort, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // Typically a second Windows session already uses the default port. Windows reports a port held
            // exclusively by another program (or reserved by Hyper-V) as "access denied", not "in use".
            logger.LogWarning(ex, "HTTPS port {Port} is not available; using a dynamic port", settings.Current.ApiPort);
            Port = await ListenAsync(0, cancellationToken);
        }

        logger.LogInformation("Peer API listening on HTTPS port {Port}", Port);
    }

    public abstract Task StopAsync(CancellationToken cancellationToken);

    /// <param name="port">0 for any free port.</param>
    /// <returns>The port actually listening.</returns>
    protected abstract Task<int> ListenAsync(int port, CancellationToken cancellationToken);
}
