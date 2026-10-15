using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using A0;
using Shorekeeper.Core.Identity;

// PC side of A0 (docs/14-android.md §4): calls the phone's TLS server the way PeerClient does.
string phone = args.Length > 0 ? args[0] : "192.168.1.135:47500";
using X509Certificate2 me = DeviceCert.Create();
DeviceId myId = DeviceCert.IdOf(me);
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
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };

Console.WriteLine($"PC DeviceId {myId.ShortForm}, calling https://{phone}");
string hello = await client.GetStringAsync(new Uri($"https://{phone}/hello"));
Console.WriteLine($"hello: {hello}");
Console.WriteLine($"phone certificate DeviceId: {serverId?.ShortForm}");
Console.WriteLine(hello.Contains(myId.Value, StringComparison.Ordinal)
    ? "OK: phone saw our client certificate"
    : "FAIL: phone did not report our DeviceId");

string host = phone.Split(':')[0];
int port = int.Parse(phone.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);

long received = 0;
await MeasureRawAsync("raw, no locks");
await MeasureTlsAsync("tls, no locks");
await client.GetAsync(new Uri($"https://{phone}/lock/on"));
await MeasureRawAsync("raw, locks held");
await MeasureTlsAsync("tls, locks held");
await client.GetAsync(new Uri($"https://{phone}/lock/off"));

async Task MeasureRawAsync(string label)
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    var watch = Stopwatch.StartNew();
    received = 0;
    try
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(host, port + 1, cancel.Token);
        await using var stream = tcp.GetStream();
        await DrainAsync(stream, cancel.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Write("(stopped after 40 s) ");
    }

    Report(label, received, watch);
}

async Task MeasureTlsAsync(string label)
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    var watch = Stopwatch.StartNew();
    received = 0;
    try
    {
        using HttpResponseMessage blob = await client.GetAsync(new Uri($"https://{phone}/blob/64"), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        await using Stream body = await blob.Content.ReadAsStreamAsync(cancel.Token);
        await DrainAsync(body, cancel.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Write("(stopped after 40 s) ");
    }

    Report(label, received, watch);
}

async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
{
    byte[] buffer = new byte[1024 * 1024];
    int read;
    while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
    {
        received += read;
    }
}

static void Report(string label, long total, Stopwatch watch) =>
    Console.WriteLine($"{label}: {total / 1048576.0:F0} MB in {watch.Elapsed.TotalSeconds:F1} s = {total / 1048576.0 / watch.Elapsed.TotalSeconds:F1} MB/s");
