using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace A0;

/// <summary>
/// The test server A0Pc measures: mTLS on <c>port</c> (GET /hello, /blob/{chunk KB} = 100 MB, /lock/on|off)
/// and plain TCP on <c>port + 1</c> (100 MB).
/// </summary>
internal sealed class BlobServer(X509Certificate2 certificate, int port, Action<string> log, Action<bool>? setLocks = null)
{
    private const long BlobSize = 100L * 1024 * 1024;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _ = ServeRawAsync(stoppingToken);
        using var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        log($"server: mTLS on {port}, plain TCP on {port + 1}");
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleAsync(client), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            log("server: stopped");
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, remote, _, _) => remote is not null,
                });
                string caller = ssl.RemoteCertificate is { } remote ? DeviceCert.IdOf(remote).Value : "";
                string path = await ReadRequestPathAsync(ssl);
                log($"server: {ssl.SslProtocol} GET {path}");

                if (path.StartsWith("/lock/", StringComparison.Ordinal))
                {
                    setLocks?.Invoke(path == "/lock/on");
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
                    return;
                }

                if (path.StartsWith("/blob/", StringComparison.Ordinal) && int.TryParse(path[6..], out int chunkKb))
                {
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {BlobSize}\r\nConnection: close\r\n\r\n"));
                    var watch = Stopwatch.StartNew();
                    byte[] chunk = new byte[chunkKb * 1024];
                    for (long sent = 0; sent < BlobSize; sent += chunk.Length)
                    {
                        await ssl.WriteAsync(chunk);
                    }

                    log($"server: blob sent at {BlobSize / 1048576.0 / watch.Elapsed.TotalSeconds:F1} MB/s");
                    return;
                }

                byte[] body = Encoding.UTF8.GetBytes($$"""{"deviceId":"{{DeviceCert.IdOf(certificate).Value}}","caller":"{{caller}}"}""");
                await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                await ssl.WriteAsync(body);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                log($"server: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task ServeRawAsync(CancellationToken stoppingToken)
    {
        using var listener = new TcpListener(IPAddress.Any, port + 1);
        listener.Start();
        try
        {
            while (true)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken);
                try
                {
                    await using NetworkStream stream = client.GetStream();
                    byte[] chunk = new byte[64 * 1024];
                    for (long sent = 0; sent < BlobSize; sent += chunk.Length)
                    {
                        await stream.WriteAsync(chunk, stoppingToken);
                    }
                }
                catch (IOException ex)
                {
                    log($"server: raw stopped: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string> ReadRequestPathAsync(Stream stream)
    {
        var header = new StringBuilder();
        byte[] one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
        {
            header.Append((char)one[0]);
        }

        string[] requestLine = header.ToString().Split("\r\n")[0].Split(' ');
        return requestLine.Length > 1 ? requestLine[1] : "";
    }
}
