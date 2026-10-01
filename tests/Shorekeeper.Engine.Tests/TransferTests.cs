using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Tests;

/// <summary>Sending and receiving files between real engines over HTTPS (docs/06-file-transfer.md).</summary>
public sealed class TransferTests : IAsyncLifetime
{
    private TestNode a = null!;
    private TestNode b = null!;

    public async ValueTask InitializeAsync()
    {
        a = await TestNode.StartAsync("A");
        b = await TestNode.StartAsync("B");
        await a.ConnectAsync(b);
    }

    public async ValueTask DisposeAsync()
    {
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Recipient_sees_the_offer_and_downloads_an_identical_copy()
    {
        byte[] content = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 7);
        string source = WriteFile(a, "build.zip", content);

        string offerId = await SendAsync(a, [source], b.Id, note: "Bản build");
        ReceivedOffer offer = await WaitForOfferAsync(b, offerId);
        Assert.Equal(InboxState.New, offer.State);
        Assert.Equal("Bản build", offer.Note);
        Assert.Equal(content.Length, offer.TotalBytes);

        ReceivedOffer done = await DownloadAsync(b, offer);

        Assert.Equal(InboxState.Completed, done.State);
        string received = Assert.Single(done.Files).FinalPath!;
        Assert.Equal(Path.Combine(b.DownloadDirectory, "build.zip"), received);
        Assert.Equal(content, await File.ReadAllBytesAsync(received, Ct));
        Assert.Equal(File.GetLastWriteTimeUtc(source).ToString("s"), File.GetLastWriteTimeUtc(received).ToString("s"));
        await WaitUntil(() => a.Offers.GetOffers().Single().State == OfferState.Done);
        Assert.Equal(RecipientState.Completed, a.Offers.GetOffers().Single().Recipients.Single().State);
    }

    [Fact]
    public async Task Folders_keep_their_structure_including_empty_ones()
    {
        string root = Path.Combine(a.WorkDirectory, "project");
        Directory.CreateDirectory(Path.Combine(root, "src", "core"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        await File.WriteAllTextAsync(Path.Combine(root, "readme.md"), "hello", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "core", "main.cs"), "class A {}", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "Thumbs.db"), "skip me", Ct);

        string offerId = await SendAsync(a, [root], b.Id);
        ReceivedOffer done = await DownloadAsync(b, await WaitForOfferAsync(b, offerId));

        Assert.Equal(InboxState.Completed, done.State);
        string received = Path.Combine(b.DownloadDirectory, "project");
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(received, "readme.md"), Ct));
        Assert.Equal("class A {}", await File.ReadAllTextAsync(Path.Combine(received, "src", "core", "main.cs"), Ct));
        Assert.True(Directory.Exists(Path.Combine(received, "empty")));
        Assert.False(File.Exists(Path.Combine(received, "Thumbs.db")));
    }

    [Fact]
    public async Task Existing_files_are_never_overwritten()
    {
        Directory.CreateDirectory(b.DownloadDirectory);
        await File.WriteAllTextAsync(Path.Combine(b.DownloadDirectory, "report.txt"), "mine", Ct);
        string source = WriteFile(a, "report.txt", "theirs"u8.ToArray());

        string offerId = await SendAsync(a, [source], b.Id);
        ReceivedOffer done = await DownloadAsync(b, await WaitForOfferAsync(b, offerId));

        Assert.Equal(Path.Combine(b.DownloadDirectory, "report (1).txt"), done.Files.Single().FinalPath);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(b.DownloadDirectory, "report.txt"), Ct));
        Assert.Equal("theirs", await File.ReadAllTextAsync(done.Files.Single().FinalPath!, Ct));
    }

    [Fact]
    public async Task Offer_from_a_peer_that_is_not_trusted_is_refused()
    {
        await using TestNode stranger = await TestNode.StartAsync("Stranger");
        stranger.Sees(b);
        await stranger.Trust.SetTrustedAsync(b.Id, "B", "HOST", Ct); // one-sided: B never accepted

        await SendAsync(stranger, [WriteFile(stranger, "x.bin", [1, 2, 3])], b.Id);

        await WaitUntil(() => stranger.Offers.GetOffers().Single().Recipients.Single().State == RecipientState.Failed);
        Assert.Empty(b.Inbox.GetOffers());
    }

    [Fact]
    public async Task Only_recipients_may_download()
    {
        await using TestNode c = await TestNode.StartAsync("C");
        await a.ConnectAsync(c);
        string offerId = await SendAsync(a, [WriteFile(a, "secret.bin", [1, 2, 3])], b.Id);

        using HttpResponseMessage response = await c.Client.SendAsync(
            a.Id, u => new HttpRequestMessage(HttpMethod.Get, new Uri(u, $"offers/{offerId}/files/f0")), TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        PeerApiException error = await Assert.ThrowsAsync<PeerApiException>(() => PeerClient.EnsureSuccessAsync(response, Ct));
        Assert.Equal(ApiErrorCodes.NotRecipient, error.Code);
    }

    [Fact]
    public async Task Changed_source_file_fails_the_download_and_is_flagged_to_the_sender()
    {
        string source = WriteFile(a, "notes.txt", "v1"u8.ToArray());
        string offerId = await SendAsync(a, [source], b.Id);
        ReceivedOffer offer = await WaitForOfferAsync(b, offerId);

        await File.WriteAllTextAsync(source, "version 2", Ct);
        ReceivedOffer done = await DownloadAsync(b, offer);

        Assert.Equal(InboxState.Failed, done.State);
        Assert.Contains("thay đổi", done.Error);
        Assert.True(a.Offers.GetOffers().Single().HasChangedFiles);
        Assert.Empty(Directory.GetFiles(b.DownloadDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Withdrawn_offer_cannot_be_downloaded()
    {
        string offerId = await SendAsync(a, [WriteFile(a, "draft.docx", [1, 2, 3])], b.Id);
        ReceivedOffer offer = await WaitForOfferAsync(b, offerId);

        await a.Offers.WithdrawAsync(offerId);

        await WaitUntil(() => b.Inbox.Get(new OfferKey(a.Id, offerId))!.State == InboxState.Withdrawn);
        b.Inbox.Download(new OfferKey(offer.SenderId, offerId));
        Assert.Equal(InboxState.Withdrawn, b.Inbox.Get(new OfferKey(a.Id, offerId))!.State);
    }

    [Fact]
    public async Task Declining_finishes_the_offer_for_the_sender()
    {
        string offerId = await SendAsync(a, [WriteFile(a, "spam.zip", [1])], b.Id);
        await WaitForOfferAsync(b, offerId);

        await b.Inbox.DeclineAsync(new OfferKey(a.Id, offerId));

        await WaitUntil(() => a.Offers.GetOffers().Single().State == OfferState.Done);
        Assert.Equal(RecipientState.Declined, a.Offers.GetOffers().Single().Recipients.Single().State);
    }

    [Fact]
    public async Task Offline_recipient_gets_the_offer_when_it_comes_online()
    {
        a.Directory.Remove(b.Id); // A does not see B and knows no address for it
        string offerId = await SendAsync(a, [WriteFile(a, "later.txt", "hi"u8.ToArray())], b.Id);
        await Task.Delay(500, Ct);
        Assert.Equal(RecipientState.Pending, a.Offers.GetOffers().Single().Recipients.Single().State);

        a.Sees(b);

        await WaitForOfferAsync(b, offerId);
        await WaitUntil(() => a.Offers.GetOffers().Single().Recipients.Single().State == RecipientState.Delivered);
    }

    [Fact]
    public async Task File_open_for_writing_is_sent_as_a_temporary_copy()
    {
        string source = WriteFile(a, "app.log", "line 1\n"u8.ToArray());
        await using var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        PreparedOffer prepared = OfferService.Prepare([source]);
        Assert.True(Assert.Single(prepared.LockedFiles).IsLocked);

        string offerId = await a.Offers.CreateAsync(prepared, [b.Id], null, snapshotLocked: true, Ct);
        ReceivedOffer done = await DownloadAsync(b, await WaitForOfferAsync(b, offerId));

        Assert.Equal(InboxState.Completed, done.State);
        Assert.Equal("line 1\n", await File.ReadAllTextAsync(done.Files.Single().FinalPath!, Ct));
    }

    [Fact]
    public async Task Locked_files_are_left_out_without_a_snapshot()
    {
        string locked = WriteFile(a, "db.mdf", [1, 2, 3]);
        string normal = WriteFile(a, "readme.txt", "ok"u8.ToArray());
        await using var writer = new FileStream(locked, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        string offerId = await a.Offers.CreateAsync(OfferService.Prepare([locked, normal]), [b.Id], null, snapshotLocked: false, Ct);
        ReceivedOffer offer = await WaitForOfferAsync(b, offerId);

        Assert.Equal("readme.txt", Assert.Single(offer.Files).RelativePath);
    }

    [Fact]
    public async Task Auto_accept_downloads_offers_within_the_limit_without_asking()
    {
        await b.Trust.SetAutoAcceptAsync(a.Id, 1024, Ct);

        string small = await SendAsync(a, [WriteFile(a, "small.txt", new byte[1000])], b.Id);
        string large = await SendAsync(a, [WriteFile(a, "large.bin", new byte[5000])], b.Id);

        await WaitUntil(() => b.Inbox.Get(new OfferKey(a.Id, small))?.State == InboxState.Completed);
        await WaitForOfferAsync(b, large);
        Assert.Equal(InboxState.New, b.Inbox.Get(new OfferKey(a.Id, large))!.State);
    }

    [Fact]
    public async Task Picked_files_download_first_and_the_rest_can_follow()
    {
        string root = Path.Combine(a.WorkDirectory, "photos");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "1.jpg"), "one", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "2.jpg"), "two", Ct);
        string offerId = await SendAsync(a, [root], b.Id);
        ReceivedOffer offer = await WaitForOfferAsync(b, offerId);
        var key = new OfferKey(a.Id, offerId);
        string first = offer.Files.Single(f => f.RelativePath == "photos/1.jpg").FileId;

        b.Inbox.Download(key, [first]);
        await WaitUntil(() => b.Inbox.Get(key)!.State == InboxState.Partial);

        Assert.True(File.Exists(Path.Combine(b.DownloadDirectory, "photos", "1.jpg")));
        Assert.False(File.Exists(Path.Combine(b.DownloadDirectory, "photos", "2.jpg")));

        b.Inbox.Download(key);
        await WaitUntil(() => b.Inbox.Get(key)!.State == InboxState.Completed);
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(b.DownloadDirectory, "photos", "2.jpg"), Ct));
    }

    [Fact]
    public async Task Sender_honours_range_and_if_range_for_resuming()
    {
        byte[] content = RandomNumberGenerator.GetBytes(100_000);
        string offerId = await SendAsync(a, [WriteFile(a, "data.bin", content)], b.Id);
        await WaitForOfferAsync(b, offerId);

        using HttpResponseMessage first = await Get(null, null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag!;

        using HttpResponseMessage resumed = await Get(40_000, etag);
        Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
        Assert.Equal(content[40_000..], await resumed.Content.ReadAsByteArrayAsync(Ct));

        // A stale ETag means "the file changed": the whole file comes back, which the downloader treats as an error.
        using HttpResponseMessage stale = await Get(40_000, new EntityTagHeaderValue("\"other\""));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);

        Task<HttpResponseMessage> Get(long? from, EntityTagHeaderValue? ifRange) =>
            b.Client.SendAsync(a.Id, u =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(u, $"offers/{offerId}/files/f0"));
                if (from is not null)
                {
                    request.Headers.Range = new RangeHeaderValue(from, null);
                    request.Headers.IfRange = new RangeConditionHeaderValue(ifRange!);
                }

                return request;
            }, TimeSpan.FromSeconds(10), Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WriteFile(TestNode node, string name, byte[] content)
    {
        string path = Path.Combine(node.WorkDirectory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static Task<string> SendAsync(TestNode sender, string[] paths, DeviceId recipient, string? note = null) =>
        sender.Offers.CreateAsync(OfferService.Prepare(paths), [recipient], note, snapshotLocked: false, Ct);

    private static async Task<ReceivedOffer> WaitForOfferAsync(TestNode recipient, string offerId)
    {
        ReceivedOffer? offer = null;
        await WaitUntil(() => (offer = recipient.Inbox.GetOffers().FirstOrDefault(o => o.OfferId == offerId)) is not null);
        return offer!;
    }

    private static async Task<ReceivedOffer> DownloadAsync(TestNode recipient, ReceivedOffer offer)
    {
        var key = new OfferKey(offer.SenderId, offer.OfferId);
        recipient.Inbox.Download(key);
        ReceivedOffer? done = null;
        await WaitUntil(() => (done = recipient.Inbox.Get(key)) is { State: not (InboxState.New or InboxState.Downloading) }, TimeSpan.FromSeconds(30));
        return done!;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            await Task.Delay(50, Ct);
        }
    }
}
