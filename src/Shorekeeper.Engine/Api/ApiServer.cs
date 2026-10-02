using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Api;

public sealed class ApiServerOptions
{
    /// <summary>Tests bind to loopback; the app listens on all interfaces.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
}

/// <summary>
/// The HTTPS endpoint other peers call (docs/05-protocol.md). Mutual TLS with the device certificate;
/// who may call what is decided by <see cref="PeerAccess"/>.
/// </summary>
public sealed class ApiServer(
    ShorekeeperEngine engine,
    SettingsService settings,
    ApiServerOptions options,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    ILogger<ApiServer> logger) : IHostedService, IAsyncDisposable
{
    private WebApplication? app;

    /// <summary>Port actually listening; 0 until started.</summary>
    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            app = await StartWebAppAsync(settings.Current.ApiPort, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // Typically a second Windows session already uses the default port. Windows reports a port held
            // exclusively by another program (or reserved by Hyper-V) as "access denied", not "in use".
            logger.LogWarning(ex, "HTTPS port {Port} is not available; using a dynamic port", settings.Current.ApiPort);
            app = await StartWebAppAsync(0, cancellationToken);
        }

        Port = new Uri(app.Urls.First()).Port;
        logger.LogInformation("Peer API listening on HTTPS port {Port}", Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (app is not null)
        {
            await app.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    private async Task<WebApplication> StartWebAppAsync(int port, CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelHttpsConfiguration();
        // Forward to the app's logging, but not ASP.NET Core's per-request lines: a 10,000-file
        // transfer would otherwise write 100,000+ log lines.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // Offer manifests list up to 10,000 entries.
            kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            kestrel.Listen(options.BindAddress, port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = engine.Identity.Certificate;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    // Self-signed device certificates: identity comes from the key hash, checked by PeerAccess.
                    https.AllowAnyClientCertificate();
                });
            });
        });

        WebApplication webApp = builder.Build();
        webApp.UsePeerAccess(services.GetRequiredService<TrustStore>(), services.GetRequiredService<GroupStore>());
        PeerApiEndpoints.Map(webApp, services);

        try
        {
            await webApp.StartAsync(cancellationToken);
            return webApp;
        }
        catch
        {
            await webApp.DisposeAsync();
            throw;
        }
    }
}

/// <summary>Hands the web host's loggers over to the app's logger factory.</summary>
internal sealed class ForwardingLoggerProvider(ILoggerFactory target) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => target.CreateLogger(categoryName);

    public void Dispose()
    {
        // The target factory belongs to the app.
    }
}