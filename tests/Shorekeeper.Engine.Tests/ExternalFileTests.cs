using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Tests;

/// <summary>Sending files the OS hands over by reference (Android content:// URIs), read without copying.</summary>
public sealed class ExternalFileTests : IAsyncLifetime
{
    private readonly FakeShared shared = new();
    private TestNode phone = null!;
    private TestNode pc = null!;

    public async ValueTask InitializeAsync()
    {
        phone = await TestNode.StartAsync("Phone", external: shared);
        pc = await TestNode.StartAsync("PC");
        await phone.ConnectAsync(pc);
    }

    public async ValueTask DisposeAsync()
    {
        await phone.DisposeAsync();
        await pc.DisposeAsync();
        shared.Dispose();
    }

    [Fact]
    public async Task Shared_items_are_sent_under_their_names_and_arrive_intact()
    {
        byte[] photo = RandomNumberGenerator.GetBytes(300_000);
        string first = shared.Add("IMG_0001.jpg", photo);
        string second = shared.Add("IMG_0001.jpg", [1, 2, 3]);

        PreparedOffer prepared = OfferService.Prepare([first, second, "test://gone"], shared);
        Assert.Equal(["IMG_0001.jpg", "IMG_0001 (1).jpg"], prepared.Files.Select(f => f.RelativePath));
        Assert.Equal(["test://gone"], prepared.Unreadable);

        string offerId = await phone.Offers.CreateAsync(prepared, [pc.Id], null, snapshotLocked: false, groupId: null, Ct);
        ReceivedOffer offer = await WaitForOfferAsync(offerId);
        var key = new OfferKey(phone.Id, offerId);
        pc.Inbox.Download(key);
        await WaitUntil(() => pc.Inbox.Get(key)!.State == InboxState.Completed);

        Assert.Equal(photo, await File.ReadAllBytesAsync(Path.Combine(pc.DownloadDirectory, "IMG_0001.jpg"), Ct));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(pc.DownloadDirectory, "IMG_0001 (1).jpg"), Ct));
        Assert.Equal(2, offer.Files.Count);
    }

    [Fact]
    public async Task Shared_items_support_ranges_and_report_changes()
    {
        byte[] content = RandomNumberGenerator.GetBytes(100_000);
        string location = shared.Add("video.mp4", content);
        string offerId = await phone.Offers.CreateAsync(OfferService.Prepare([location], shared), [pc.Id], null, snapshotLocked: false, groupId: null, Ct);
        await WaitForOfferAsync(offerId);

        using (HttpResponseMessage resumed = await GetAsync(offerId, 60_000))
        {
            Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
            Assert.Equal(content[60_000..], await resumed.Content.ReadAsByteArrayAsync(Ct));
        }

        shared.Replace(location, [9, 9, 9]);
        using HttpResponseMessage changed = await GetAsync(offerId, null);
        Assert.Equal(HttpStatusCode.PreconditionFailed, changed.StatusCode);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> GetAsync(string offerId, long? from) =>
        pc.Client.SendAsync(phone.Id, u =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(u, $"offers/{offerId}/files/f0"));
            if (from is not null)
            {
                request.Headers.Range = new RangeHeaderValue(from, null);
            }

            return request;
        }, TimeSpan.FromSeconds(10), Ct);

    private async Task<ReceivedOffer> WaitForOfferAsync(string offerId)
    {
        ReceivedOffer? offer = null;
        await WaitUntil(() => (offer = pc.Inbox.GetOffers().FirstOrDefault(o => o.OfferId == offerId)) is not null);
        return offer!;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    /// <summary>"test://n" locations backed by temporary files, with names that may repeat like photos do.</summary>
    private sealed class FakeShared : IExternalFileSource, IDisposable
    {
        private readonly string folder = Directory.CreateTempSubdirectory("shorekeeper-shared-").FullName;
        private readonly Dictionary<string, (string Name, string Path)> items = [];

        public string Add(string name, byte[] content)
        {
            string location = $"test://{items.Count}";
            string path = Path.Combine(folder, items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllBytes(path, content);
            items[location] = (name, path);
            return location;
        }

        public void Replace(string location, byte[] content)
        {
            File.WriteAllBytes(items[location].Path, content);
            File.SetLastWriteTimeUtc(items[location].Path, DateTime.UtcNow.AddMinutes(1));
        }

        public bool Owns(string location) => location.StartsWith("test://", StringComparison.Ordinal);

        public ExternalFileInfo? Describe(string location) =>
            items.TryGetValue(location, out var item)
                ? new ExternalFileInfo(item.Name, new FileInfo(item.Path).Length, File.GetLastWriteTimeUtc(item.Path))
                : null;

        public Stream OpenRead(string location) =>
            items.TryGetValue(location, out var item) ? File.OpenRead(item.Path) : throw new IOException("Gone.");

        public void Dispose() => Directory.Delete(folder, recursive: true);
    }
}
