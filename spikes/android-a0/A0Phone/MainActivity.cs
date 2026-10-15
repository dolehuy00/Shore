using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Android.Net.Wifi;
using Android.Widget;
using Dapper;
using Microsoft.Data.Sqlite;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Build = Android.OS.Build;
using ProtocolType = System.Net.Sockets.ProtocolType;
using Stopwatch = System.Diagnostics.Stopwatch;
using WifiMode = Android.Net.WifiMode;

namespace A0;

/// <summary>
/// A0 spike (docs/14-android.md §4): checks on a real phone what Shorekeeper depends on.
/// Results go to the screen and to logcat (tag "A0").
/// </summary>
[Activity(Name = "dev.shorekeeper.a0.MainActivity", Label = "Shorekeeper A0", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    private const int ServerPort = 47500;
    private const string PcAddress = "192.168.1.87";
    private static readonly IPAddress Group = IPAddress.Parse("239.255.47.47");

    private TextView? output;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        output = new TextView(this) { TextSize = 12 };
        var scroll = new ScrollView(this);
        scroll.AddView(output);
        SetContentView(scroll);
        _ = Task.Run(RunAsync);
    }

    private void Log(string line)
    {
        Android.Util.Log.Info("A0", line);
        RunOnUiThread(() => output?.Append(line + "\n"));
    }

    private async Task RunAsync()
    {
        Log($"Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), {Build.Model}");
        await Step("T4 SQLite + Dapper", SqliteAsync);

        X509Certificate2? me = null;
        await Step("Certificate", () =>
        {
            me = DeviceCert.Create();
            Log($"phone DeviceId {DeviceCert.IdOf(me).ShortForm}, private key: {me.HasPrivateKey}");
            return Task.CompletedTask;
        });
        if (me is null)
        {
            return;
        }

        _ = Task.Run(() => ServeAsync(me));
        await Step("T2 mTLS client -> PC Shorekeeper (adb reverse 47471)", () => CallPcAsync(me));
        await Step("T3 multicast + probe", () => MulticastAsync(me));
        Log("Done. TLS server keeps running for 5 minutes for the PC test.");
    }

    private async Task Step(string name, Func<Task> run)
    {
        Log($"== {name}");
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            Log($"FAIL {ex.GetType().Name}: {ex.Message}");
            for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                Log($"  inner {inner.GetType().Name}: {inner.Message}");
            }
        }
    }

    // ───────────── T1: TLS server with required client certificate ─────────────

    private async Task ServeAsync(X509Certificate2 me)
    {
        _ = Task.Run(ServeRawAsync);
        Log($"== T1 mTLS server listening on {ServerPort}");
        using var listener = new TcpListener(IPAddress.Any, ServerPort);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(() => HandleAsync(client, me));
            }
        }
        catch (OperationCanceledException)
        {
            Log("T1 server stopped");
        }
    }

    private async Task HandleAsync(TcpClient client, X509Certificate2 me)
    {
        using (client)
        {
            try
            {
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = me,
                    ClientCertificateRequired = true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
                });
                string? caller = ssl.RemoteCertificate is { } remote ? DeviceCert.IdOf(remote).Value : null;
                string path = await ReadRequestPathAsync(ssl);
                Log($"T1 {client.Client.RemoteEndPoint} {ssl.SslProtocol} GET {path}, client cert: {(caller is null ? "NONE" : caller[..8])}");

                // /lock/on, /lock/off: Wi-Fi high-performance lock + partial wake lock, as a transfer would hold.
                if (path.StartsWith("/lock/", StringComparison.Ordinal))
                {
                    SetLocks(path == "/lock/on");
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
                    return;
                }

                // /blob/{chunk KB}: 100 MB written in chunks of that size.
                if (path.StartsWith("/blob/", StringComparison.Ordinal) && int.TryParse(path[6..], out int chunkKb))
                {
                    const long size = 100L * 1024 * 1024;
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {size}\r\nConnection: close\r\n\r\n"));
                    Log($"T1 blob/{chunkKb}: headers written");
                    byte[] chunk = new byte[chunkKb * 1024];
                    var watch = Stopwatch.StartNew();
                    long nextReport = 0;
                    for (long sent = 0; sent < size; sent += chunk.Length)
                    {
                        if (sent >= nextReport)
                        {
                            Log($"T1 blob/{chunkKb}: {sent / 1048576} MB after {watch.Elapsed.TotalSeconds:F1} s");
                            nextReport += 20L * 1048576;
                        }

                        await ssl.WriteAsync(chunk);
                    }

                    Log($"T1 blob/{chunkKb} sent: {size / 1048576.0 / watch.Elapsed.TotalSeconds:F1} MB/s");
                    return;
                }

                string json = $$"""{"deviceId":"{{DeviceCert.IdOf(me).Value}}","caller":"{{caller}}","protocol":"{{ssl.SslProtocol}}"}""";
                byte[] body = Encoding.UTF8.GetBytes(json);
                await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                await ssl.WriteAsync(body);
            }
            catch (Exception ex)
            {
                Log($"T1 FAIL {ex.GetType().Name}: {ex.Message} {ex.InnerException?.Message}");
            }
        }
    }

    private WifiManager.WifiLock? wifiLock;
    private Android.OS.PowerManager.WakeLock? wakeLock;

    private void SetLocks(bool on)
    {
        if (on)
        {
            var wifi = (WifiManager)GetSystemService(WifiService)!;
#pragma warning disable CA1422 // Deprecated in API 34, but the alternative only works with the screen on: this spike measures it.
            wifiLock ??= wifi.CreateWifiLock(WifiMode.FullHighPerf, "shorekeeper-a0");
#pragma warning restore CA1422
            var power = (Android.OS.PowerManager)GetSystemService(PowerService)!;
            wakeLock ??= power.NewWakeLock(Android.OS.WakeLockFlags.Partial, "shorekeeper:a0");
            wifiLock!.Acquire();
            wakeLock!.Acquire();
        }
        else
        {
            wifiLock?.Release();
            wakeLock?.Release();
        }

        Log($"T1 locks {(on ? "acquired" : "released")}");
    }

    /// <summary>Plain TCP on the next port: 100 MB without TLS, to tell Wi-Fi speed from TLS cost.</summary>
    private async Task ServeRawAsync()
    {
        using var listener = new TcpListener(IPAddress.Any, ServerPort + 1);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            while (true)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                try
                {
                    await using NetworkStream stream = client.GetStream();
                    byte[] chunk = new byte[64 * 1024];
                    var watch = Stopwatch.StartNew();
                    const long size = 100L * 1024 * 1024;
                    for (long sent = 0; sent < size; sent += chunk.Length)
                    {
                        await stream.WriteAsync(chunk);
                    }

                    Log($"T1 raw sent: {size / 1048576.0 / watch.Elapsed.TotalSeconds:F1} MB/s");
                }
                catch (IOException ex)
                {
                    Log($"T1 raw stopped: {ex.Message}");
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

    // ───────────── T2: TLS client with our certificate, like PeerClient ─────────────

    private async Task CallPcAsync(X509Certificate2 me)
    {
        DeviceId? serverId = null;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                LocalCertificateSelectionCallback = (_, _, _, _, _) => me,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    serverId = certificate is null ? null : DeviceCert.IdOf(certificate);
                    return certificate is not null;
                },
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        // Through "adb reverse tcp:47471 tcp:47471": the laptop's Wi-Fi is a Public network, so its firewall blocks the phone.
        string hello = await client.GetStringAsync(new Uri("https://127.0.0.1:47471/api/v1/hello"));
        Log($"T2 hello: {hello}");
        Log(serverId is { } id && hello.Contains(id.Value, StringComparison.Ordinal)
            ? $"T2 OK: PC certificate DeviceId {id.ShortForm} matches its hello"
            : $"T2 FAIL: certificate DeviceId {serverId?.ShortForm} does not match");
    }

    // ───────────── T3: multicast presence and unicast probe ─────────────

    private async Task MulticastAsync(X509Certificate2 me)
    {
        var wifi = (WifiManager)GetSystemService(WifiService)!;
        WifiManager.MulticastLock lockHandle = wifi.CreateMulticastLock("shorekeeper-a0")!;
        lockHandle.SetReferenceCounted(false);
        lockHandle.Acquire();
        try
        {
            IPAddress? wlan = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Name == "wlan0")
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            Log($"T3 wlan0 address: {wlan?.ToString() ?? "not found"}");

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, 47470));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                wlan is null ? new MulticastOption(Group) : new MulticastOption(Group, wlan));

            byte[] probe = PresenceCodec.Encode(new PresencePacket
            {
                Type = PresencePacketTypes.Probe,
                Id = DeviceCert.IdOf(me).Value,
                Name = "A0 " + Build.Model,
                Host = Build.Model ?? "phone",
                Os = "android",
                Port = ServerPort,
            });
            await socket.SendToAsync(probe, new IPEndPoint(IPAddress.Parse(PcAddress), 47470));
            await socket.SendToAsync(probe, new IPEndPoint(Group, 47470));
            Log("T3 probe sent to PC (unicast) and to the group; listening 20 s");

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            byte[] buffer = new byte[2048];
            int heard = 0;
            try
            {
                while (true)
                {
                    SocketReceiveFromResult result = await socket.ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0), stop.Token);
                    if (PresenceCodec.TryDecode(buffer.AsSpan(0, result.ReceivedBytes), out PresencePacket? packet)
                        && packet.Id != DeviceCert.IdOf(me).Value)
                    {
                        heard++;
                        Log($"T3 {packet.Type} from {result.RemoteEndPoint}: {packet.Name} ({packet.Host})");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            Log(heard > 0 ? $"T3 OK: {heard} presence packets" : "T3 FAIL: no presence packet heard");
        }
        finally
        {
            lockHandle.Release();
        }
    }

    // ───────────── T4: SQLite + Dapper in a trimmed Release build ─────────────

    private async Task SqliteAsync()
    {
        string path = Path.Combine(FilesDir!.AbsolutePath, "a0.db");
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await connection.ExecuteAsync("CREATE TABLE IF NOT EXISTS Peers (DeviceId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, TrustLevel INTEGER NOT NULL);");
        await connection.ExecuteAsync(
            "INSERT OR REPLACE INTO Peers (DeviceId, DisplayName, TrustLevel) VALUES (@Id, @Name, 1);",
            new { Id = "a0", Name = "Lê Huy" });
        PeerRow row = (await connection.QueryAsync<PeerRow>("SELECT DeviceId AS Id, DisplayName, TrustLevel AS Level FROM Peers;")).Single();
        Log(row is { Id: "a0", DisplayName: "Lê Huy", Level: 1 } ? "T4 OK: Dapper mapped the row" : $"T4 FAIL: {row.Id}/{row.DisplayName}/{row.Level}");
    }

    private sealed class PeerRow
    {
        public string Id { get; init; } = "";

        public string DisplayName { get; init; } = "";

        public long Level { get; init; }
    }
}
