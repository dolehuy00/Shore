using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Kestrel;

/// <summary>
/// The peer API on Kestrel, for desktops: every request under <c>/api/v1</c> goes to <see cref="PeerApi"/>,
/// so routes and access rules are the same as with <see cref="MiniHttpServer"/>.
/// </summary>
public sealed class KestrelApiServer(
    ShorekeeperEngine engine,
    SettingsService settings,
    ApiServerOptions options,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    ILogger<KestrelApiServer> logger) : ApiServer(settings, logger), IAsyncDisposable
{
    private WebApplication? app;

    public override async Task StopAsync(CancellationToken cancellationToken)
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

    protected override async Task<int> ListenAsync(int port, CancellationToken cancellationToken)
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
            kestrel.Limits.MaxRequestBodySize = MiniHttpServer.MaxBodyBytes;
            kestrel.Listen(options.BindAddress, port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = engine.Identity.Certificate;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    // Self-signed device certificates: identity comes from the key hash, checked by PeerApi.
                    https.AllowAnyClientCertificate();
                });
            });
        });

        WebApplication webApp = builder.Build();
        PeerApi api = PeerApiEndpoints.Create(services);
        webApp.Run(context => HandleAsync(api, context, webApp.Lifetime.ApplicationStopping));

        try
        {
            await webApp.StartAsync(cancellationToken);
        }
        catch
        {
            await webApp.DisposeAsync();
            throw;
        }

        app = webApp;
        return new Uri(webApp.Urls.First()).Port;
    }

    /// <param name="stopping">
    /// Stopping waits for the requests in flight: long polls (a member's group state, up to 20 s) end here instead,
    /// as with <see cref="MiniHttpServer"/>.
    /// </param>
    private static async Task HandleAsync(PeerApi api, HttpContext context, CancellationToken stopping)
    {
        string basePath = PeerClient.ApiBasePath.TrimEnd('/');
        string path = context.Request.Path.Value ?? "";
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        PeerResponse response;
        try
        {
            response = context.Connection.ClientCertificate is not { } certificate
                ? PeerResponse.Problem(HttpStatusCode.Unauthorized, ApiErrorCodes.NotTrusted)
                : await api.HandleAsync(new PeerRequest
                {
                    Method = context.Request.Method,
                    Path = path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase) ? path[basePath.Length..] : "",
                    Query = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    Caller = DeviceIdentity.FromCertificate(certificate),
                    RemoteAddress = context.Connection.RemoteIpAddress ?? IPAddress.None,
                    Body = context.Request.Body,
                    Aborted = aborted.Token,
                });
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            context.Abort();
            return;
        }

        IResult result = response switch
        {
            JsonPeerResponse json => Results.Json(json.Value, PeerJson.Options, statusCode: json.Status),
            ProblemPeerResponse problem => Results.Problem(
                statusCode: problem.Status, title: problem.Code, extensions: new Dictionary<string, object?> { ["code"] = problem.Code }),
            FilePeerResponse file => Results.File(
                file.Content, file.ContentType, lastModified: file.LastModified,
                entityTag: file.ETag is null ? null : EntityTagHeaderValue.Parse(file.ETag), enableRangeProcessing: true),
            _ => Results.StatusCode(response.Status),
        };
        await result.ExecuteAsync(context);
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

public static class KestrelServiceCollectionExtensions
{
    /// <summary>Serves the peer API with Kestrel instead of <see cref="MiniHttpServer"/>. Call after <c>AddPeerApi</c>.</summary>
    public static IServiceCollection AddKestrelPeerApi(this IServiceCollection services) =>
        services.Replace(ServiceDescriptor.Singleton<ApiServer, KestrelApiServer>());
}
