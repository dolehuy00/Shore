using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Api;

/// <summary>None of the peer's known addresses answered.</summary>
public sealed class PeerUnreachableException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>The peer answered with an error; <see cref="Code"/> is one of <see cref="ApiErrorCodes"/>.</summary>
public sealed class PeerApiException(int status, string? code) : Exception($"Peer returned {status} ({code ?? "no code"}).")
{
    public int Status { get; } = status;

    public string? Code { get; } = code;
}

/// <summary>
/// Calls other peers over mutual TLS. The server certificate must hash to the expected DeviceId,
/// so talking to the wrong machine fails the TLS handshake (docs/04-identity-security.md §2).
/// </summary>
public sealed class PeerClient(
    ShorekeeperEngine engine,
    PeerDirectory directory,
    TrustStore trust,
    TimeProvider timeProvider,
    ILogger<PeerClient> logger) : IDisposable
{
    public const string ApiBasePath = "/api/v1/";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecordEvery = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<DeviceId, HttpClient> clients = new();
    private readonly ConcurrentDictionary<DeviceId, (IPEndPoint Endpoint, DateTimeOffset At)> lastRecorded = new();

    /// <summary>Sends a request to a peer, trying its current address first, then addresses that worked before.</summary>
    /// <param name="timeout">Per attempt; long-polls pass more than the server's wait.</param>
    public async Task<HttpResponseMessage> SendAsync(
        DeviceId peer, Func<Uri, HttpRequestMessage> createRequest, TimeSpan timeout, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPEndPoint> endpoints = await GetEndpointsAsync(peer, cancellationToken);
        HttpClient client = clients.GetOrAdd(peer, id => new HttpClient(CreateHandler(cert => DeviceIdentity.FromCertificate(cert) == id)));

        Exception? lastError = null;
        foreach (IPEndPoint endpoint in endpoints)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(timeout);
            try
            {
                using HttpRequestMessage request = createRequest(BaseUri(endpoint));
                HttpResponseMessage response = await client.SendAsync(request, attempt.Token);
                await RememberEndpointAsync(peer, endpoint);
                return response;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }

            logger.LogDebug(lastError, "Peer {Peer} not reachable at {Endpoint}", peer, endpoint);
        }

        throw new PeerUnreachableException($"Peer {peer.ShortForm} is not reachable.", lastError);
    }

    /// <summary>Asks an address who it is; used to add a machine by host name or IP.</summary>
    public async Task<(HelloResponse Hello, DeviceId DeviceId)> HelloAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        DeviceId? seen = null;
        using var client = new HttpClient(CreateHandler(cert =>
        {
            seen = DeviceIdentity.FromCertificate(cert);
            return true;
        }));
        client.Timeout = TimeSpan.FromSeconds(5);

        HelloResponse hello = await client.GetFromJsonAsync<HelloResponse>(new Uri(BaseUri(endpoint), "hello"), cancellationToken)
            ?? throw new PeerApiException(200, ApiErrorCodes.InvalidRequest);

        // The claimed id must match the certificate actually presented.
        if (seen is not { } id || !DeviceId.TryParse(hello.DeviceId, out DeviceId claimed) || claimed != id)
        {
            throw new PeerApiException(200, ApiErrorCodes.InvalidRequest);
        }

        return (hello, id);
    }

    /// <summary>Reads a JSON body, turning problem+json errors into <see cref="PeerApiException"/>.</summary>
    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try
            {
                using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                code = problem.RootElement.TryGetProperty("code", out JsonElement element) ? element.GetString() : null;
            }
            catch (JsonException)
            {
            }

            throw new PeerApiException((int)response.StatusCode, code);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new PeerApiException((int)response.StatusCode, ApiErrorCodes.InvalidRequest);
    }

    public static Uri BaseUri(IPEndPoint endpoint) => new($"https://{endpoint}{ApiBasePath}");

    public void Dispose()
    {
        foreach (HttpClient client in clients.Values)
        {
            client.Dispose();
        }
    }

    private async Task<IReadOnlyList<IPEndPoint>> GetEndpointsAsync(DeviceId peer, CancellationToken cancellationToken)
    {
        var endpoints = new List<IPEndPoint>();
        if (directory.Snapshot().FirstOrDefault(p => p.DeviceId == peer) is { ApiPort: > 0 } live)
        {
            endpoints.Add(new IPEndPoint(live.Address, live.ApiPort));
        }

        endpoints.AddRange(await trust.GetEndpointsAsync(peer, cancellationToken));
        IReadOnlyList<IPEndPoint> distinct = [.. endpoints.Distinct()];
        return distinct.Count > 0 ? distinct : throw new PeerUnreachableException($"No known address for peer {peer.ShortForm}.");
    }

    private async Task RememberEndpointAsync(DeviceId peer, IPEndPoint endpoint)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (lastRecorded.TryGetValue(peer, out var last) && last.Endpoint.Equals(endpoint) && now - last.At < RecordEvery)
        {
            return;
        }

        lastRecorded[peer] = (endpoint, now);
        await trust.RecordEndpointAsync(peer, endpoint, "known");
    }

    private SocketsHttpHandler CreateHandler(Func<X509Certificate2, bool> acceptServer) => new()
    {
        UseProxy = false,
        ConnectTimeout = ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        SslOptions = new SslClientAuthenticationOptions
        {
            // Always present our device certificate, even though the server lists no issuer we match.
            LocalCertificateSelectionCallback = (_, _, _, _, _) => engine.Identity.Certificate,
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && acceptServer(certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData())),
        },
    };
}
