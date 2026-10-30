using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Api;

/// <summary>
/// A small HTTPS/1.1 server for the peer API where ASP.NET Core does not run (Android, docs/14-android.md §3.1).
/// Only Shorekeeper calls it, so it supports what <see cref="PeerClient"/> sends: one request at a time per
/// connection with keep-alive, Content-Length or chunked bodies, and one <c>Range</c> (with <c>If-Range</c>) for files.
/// </summary>
public sealed class MiniHttpServer(
    ShorekeeperEngine engine,
    SettingsService settings,
    ApiServerOptions options,
    IServiceProvider services,
    ILogger<MiniHttpServer> logger) : ApiServer(settings, logger), IDisposable
{
    /// <summary>Offer manifests list up to 10,000 entries.</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    /// <summary>
    /// What a device that is neither a contact nor in a group with us may send: asking to connect or to join takes a
    /// few hundred bytes, and anything else is refused anyway; it must not get to fill memory first.
    /// </summary>
    public const int MaxStrangerBodyBytes = 64 * 1024;

    public const int MaxConnections = 256;

    /// <summary>One device cannot take every connection (a computer downloading uses a handful, plus long polls).</summary>
    public const int MaxConnectionsPerAddress = 32;

    private const int CopyBufferBytes = 128 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(130);

    /// <summary>A body that has not arrived by then is not coming: the connection is not held open by a trickle.</summary>
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource stopping = new();
    private TcpListener? listener;
    private PeerApi? api;
    private Task? acceptLoop;
    private int connections;
    private readonly ConcurrentDictionary<IPAddress, int> connectionsFrom = new();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        listener?.Stop();
        if (acceptLoop is not null)
        {
            await acceptLoop;
        }
    }

    public void Dispose() => stopping.Dispose();

    protected override Task<int> ListenAsync(int port, CancellationToken cancellationToken)
    {
        var started = new TcpListener(options.BindAddress, port);
        started.Start();
        listener = started;
        api = PeerApiEndpoints.Create(services);
        acceptLoop = AcceptAsync(started, stopping.Token);
        return Task.FromResult(((IPEndPoint)started.LocalEndpoint).Port);
    }

    private async Task AcceptAsync(TcpListener accepting, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await accepting.AcceptSocketAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                || (ex is SocketException && token.IsCancellationRequested))
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Accept failed");
                continue;
            }

            IPAddress from = RemoteOf(socket);
            if (Interlocked.Increment(ref connections) > MaxConnections
                | connectionsFrom.AddOrUpdate(from, 1, (_, n) => n + 1) > MaxConnectionsPerAddress)
            {
                Release(from);
                socket.Dispose();
                continue;
            }

            _ = Task.Run(() => ServeConnectionAsync(socket, from, token), CancellationToken.None);
        }
    }

    private static IPAddress RemoteOf(Socket socket)
    {
        IPAddress remote = ((IPEndPoint)socket.RemoteEndPoint!).Address;
        return remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
    }

    private void Release(IPAddress from)
    {
        Interlocked.Decrement(ref connections);
        if (connectionsFrom.AddOrUpdate(from, 0, (_, n) => n - 1) <= 0)
        {
            connectionsFrom.TryRemove(KeyValuePair.Create(from, 0));
        }
    }

    private async Task ServeConnectionAsync(Socket socket, IPAddress remote, CancellationToken token)
    {
        try
        {
            socket.NoDelay = true;
            await using var network = new NetworkStream(socket, ownsSocket: true);
            await using var ssl = new SslStream(network);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                handshake.CancelAfter(HandshakeTimeout);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = engine.Identity.Certificate,
                    ClientCertificateRequired = true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    // Self-signed device certificates: the caller is its key hash, and PeerApi decides what it may do.
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
                }, handshake.Token);
            }

            if (ssl.RemoteCertificate is not { } certificate)
            {
                return;
            }

            DeviceId caller = DeviceIdentity.FromCertificate(
                certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
            var reader = new HttpRequestReader(ssl);
            TimeSpan idle = FirstRequestTimeout;
            while (!token.IsCancellationRequested)
            {
                HttpRequestHead? head;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    wait.CancelAfter(idle);
                    head = await reader.ReadHeadAsync(wait.Token);
                }

                if (head is null)
                {
                    return;
                }

                idle = KeepAliveTimeout;
                bool known = api!.IsKnown(caller);
                byte[]? body;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    wait.CancelAfter(BodyTimeout);
                    body = await reader.ReadBodyAsync(head, known ? MaxBodyBytes : MaxStrangerBodyBytes, wait.Token);
                }

                if (body is null)
                {
                    await WriteAsync(ssl, head, PeerResponse.Problem(HttpStatusCode.RequestEntityTooLarge, ApiErrorCodes.InvalidRequest), token);
                    return;
                }

                PeerResponse response = await HandleAsync(head, body, caller, remote, token);
                await WriteAsync(ssl, head, response, token);
                if (head.CloseRequested)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or InvalidDataException or OperationCanceledException)
        {
            // Hung up, timed out, failed the handshake or sent something that is not HTTP: just close.
            logger.LogDebug(ex, "Peer API connection closed");
        }
        finally
        {
            Release(remote);
        }
    }

    private async Task<PeerResponse> HandleAsync(HttpRequestHead head, byte[] body, DeviceId caller, IPAddress remote, CancellationToken token)
    {
        string basePath = PeerClient.ApiBasePath.TrimEnd('/');
        var request = new PeerRequest
        {
            Method = head.Method,
            Path = head.Path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase) ? head.Path[basePath.Length..] : "",
            Query = head.Query,
            Caller = caller,
            RemoteAddress = remote,
            Body = new MemoryStream(body, writable: false),
            Aborted = token,
        };

        try
        {
            return await api!.HandleAsync(request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Peer API {Method} {Path} failed", head.Method, head.Path);
            return PeerResponse.Problem(HttpStatusCode.InternalServerError, ApiErrorCodes.Internal);
        }
    }

    private static async Task WriteAsync(Stream stream, HttpRequestHead head, PeerResponse response, CancellationToken token)
    {
        switch (response)
        {
            case JsonPeerResponse json:
                await WriteBodyAsync(stream, json.Status, "application/json; charset=utf-8",
                    JsonSerializer.SerializeToUtf8Bytes(json.Value, json.Type, PeerJson.Options), token);
                break;
            case ProblemPeerResponse problem:
                await WriteBodyAsync(stream, problem.Status, "application/problem+json", PeerJson.Problem(problem.Status, problem.Code), token);
                break;
            case EmptyPeerResponse empty:
                await WriteHeadAsync(stream, empty.Status, empty.Status == 204 ? [] : [("Content-Length", "0")], token);
                break;
            case FilePeerResponse file:
                await using (file.Content)
                {
                    await WriteFileAsync(stream, head, file, token);
                }

                break;
        }
    }

    private static async Task WriteBodyAsync(Stream stream, int status, string contentType, byte[] body, CancellationToken token)
    {
        await WriteHeadAsync(stream, status, [("Content-Type", contentType), ("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture))], token);
        await stream.WriteAsync(body, token);
    }

    /// <summary>Whole file, or the one range asked for when <c>If-Range</c> (if any) still matches the file's ETag.</summary>
    private static async Task WriteFileAsync(Stream stream, HttpRequestHead head, FilePeerResponse file, CancellationToken token)
    {
        long length = file.Content.Length;
        List<(string, string)> headers = [("Content-Type", file.ContentType), ("Accept-Ranges", "bytes")];
        if (file.ETag is not null)
        {
            headers.Add(("ETag", file.ETag));
        }

        if (file.LastModified is { } modified)
        {
            headers.Add(("Last-Modified", modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)));
        }

        long from = 0;
        long count = length;
        int status = 200;
        if (RequestedRange(head, file.ETag) is { } range)
        {
            if (range.From >= length)
            {
                headers.Add(("Content-Range", $"bytes */{length}"));
                headers.Add(("Content-Length", "0"));
                await WriteHeadAsync(stream, 416, headers, token);
                return;
            }

            from = range.From;
            long to = Math.Min(range.To ?? length - 1, length - 1);
            count = to - from + 1;
            status = 206;
            headers.Add(("Content-Range", $"bytes {from}-{to}/{length}"));
            file.Content.Seek(from, SeekOrigin.Begin);
        }

        headers.Add(("Content-Length", count.ToString(CultureInfo.InvariantCulture)));
        await WriteHeadAsync(stream, status, headers, token);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (count > 0)
            {
                int read = await file.Content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), token);
                if (read == 0)
                {
                    // The file got shorter while being sent; the caller notices the missing bytes.
                    throw new IOException("File ended before its announced length.");
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), token);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A single <c>bytes=from-[to]</c> range; anything else means the whole file.</summary>
    private static (long From, long? To)? RequestedRange(HttpRequestHead head, string? eTag)
    {
        if (!head.Headers.TryGetValue("Range", out string? range) || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            || (head.Headers.TryGetValue("If-Range", out string? ifRange) && ifRange != eTag))
        {
            return null;
        }

        string[] parts = range[6..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long from))
        {
            return null;
        }

        if (parts[1].Length == 0)
        {
            return (from, null);
        }

        return long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long to) && to >= from ? (from, to) : null;
    }

    private static async Task WriteHeadAsync(Stream stream, int status, IEnumerable<(string Name, string Value)> headers, CancellationToken token)
    {
        var text = new StringBuilder(256);
        text.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n");
        foreach ((string name, string value) in headers)
        {
            text.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }

        text.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()), token);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        206 => "Partial Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        410 => "Gone",
        412 => "Precondition Failed",
        413 => "Payload Too Large",
        416 => "Range Not Satisfiable",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "",
    };
}
