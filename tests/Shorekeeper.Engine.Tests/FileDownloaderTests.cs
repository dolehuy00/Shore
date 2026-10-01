using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Tests;

public sealed class FileDownloaderTests : IDisposable
{
    private static readonly EntityTagHeaderValue ETag = new("\"v1\"");

    private readonly TempAppFolder folder = new();
    private readonly byte[] content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);
    private readonly FileDownloader downloader = new(TimeProvider.System);
    private readonly List<(long Offset, EntityTagHeaderValue? IfRange)> requests = [];

    public void Dispose() => folder.Dispose();

    private string TempPath => Path.Combine(folder.Root, "file.skpart");

    [Fact]
    public async Task Downloads_the_whole_file_and_returns_its_hash()
    {
        string hash = await downloader.DownloadAsync(Serve(), TempPath, content.Length, TimeSpan.FromSeconds(5), null, Ct);

        Assert.Equal(content, await File.ReadAllBytesAsync(TempPath, Ct));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), hash);
        Assert.Single(requests);
    }

    [Fact]
    public async Task Resumes_from_the_received_byte_after_the_connection_drops()
    {
        const int dropAfter = 1_234_567;

        string hash = await downloader.DownloadAsync(Serve(dropFirstAfter: dropAfter), TempPath, content.Length, TimeSpan.FromSeconds(10), null, Ct);

        Assert.Equal(content, await File.ReadAllBytesAsync(TempPath, Ct));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), hash);
        Assert.Equal(2, requests.Count);
        Assert.Equal((0L, (EntityTagHeaderValue?)null), requests[0]);
        Assert.Equal(dropAfter, requests[1].Offset);
        Assert.Equal(ETag, requests[1].IfRange);
    }

    [Fact]
    public async Task Reports_reconnecting_while_the_sender_is_unreachable()
    {
        var reports = new List<DownloadProgress>();
        int calls = 0;
        RangeRequester flaky = (offset, ifRange, ct) =>
            ++calls <= 2 ? throw new HttpRequestException("unreachable") : Serve()(offset, ifRange, ct);

        await downloader.DownloadAsync(flaky, TempPath, content.Length, TimeSpan.FromSeconds(10), new SyncProgress(reports.Add), Ct);

        Assert.Contains(reports, r => r.ReconnectingUntil is not null);
        Assert.Null(reports[^1].ReconnectingUntil);
        Assert.Equal(content.Length, reports[^1].BytesReceived);
    }

    [Fact]
    public async Task Gives_up_when_the_reconnect_window_is_exceeded()
    {
        RangeRequester down = (_, _, _) => throw new HttpRequestException("unreachable");

        var error = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            downloader.DownloadAsync(down, TempPath, content.Length, TimeSpan.FromSeconds(2), null, Ct));

        Assert.Contains("Mất kết nối", error.Message);
    }

    [Fact]
    public async Task Fails_when_the_file_changed_while_resuming()
    {
        int calls = 0;
        RangeRequester changed = (offset, ifRange, ct) =>
            ++calls == 1
                ? Serve(dropFirstAfter: 1000)(offset, ifRange, ct)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }); // If-Range mismatch

        var error = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            downloader.DownloadAsync(changed, TempPath, content.Length, TimeSpan.FromSeconds(10), null, Ct));

        Assert.Contains("thay đổi", error.Message);
    }

    [Fact]
    public async Task Sender_errors_are_not_retried()
    {
        RangeRequester closed = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)
        {
            Content = JsonContent.Create(new { code = ApiErrorCodes.OfferClosed }),
        });

        var error = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            downloader.DownloadAsync(closed, TempPath, content.Length, TimeSpan.FromSeconds(10), null, Ct));

        Assert.Contains("thu hồi", error.Message);
    }

    [Fact]
    public async Task Empty_file_needs_no_request()
    {
        string hash = await downloader.DownloadAsync(Serve(), TempPath, 0, TimeSpan.FromSeconds(5), null, Ct);

        Assert.Empty(requests);
        Assert.Equal(0, new FileInfo(TempPath).Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), hash);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A sender that honours Range and can drop the connection once, mid-stream.</summary>
    private RangeRequester Serve(int? dropFirstAfter = null)
    {
        bool dropped = false;
        return (offset, ifRange, _) =>
        {
            requests.Add((offset, ifRange));
            byte[] rest = content[(int)offset..];
            Stream body = new MemoryStream(rest);
            if (dropFirstAfter is { } limit && !dropped)
            {
                dropped = true;
                body = new DroppingStream(body, limit);
            }

            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            };
            response.Headers.ETag = ETag;
            return Task.FromResult(response);
        };
    }

    /// <summary>Delivers <c>limit</c> bytes, then fails like a reset connection.</summary>
    private sealed class DroppingStream(Stream inner, int limit) : Stream
    {
        private int delivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (delivered >= limit)
            {
                throw new IOException("Connection reset.");
            }

            int read = inner.Read(buffer, offset, Math.Min(count, limit - delivered));
            delivered += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; tests need reports in order.</summary>
    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
