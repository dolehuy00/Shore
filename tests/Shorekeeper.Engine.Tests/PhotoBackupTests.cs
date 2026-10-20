using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

/// <summary>"Tự gửi ảnh" between real engines (docs/06-file-transfer.md §11).</summary>
public sealed class PhotoBackupTests : IAsyncLifetime
{
    private readonly FakeLibrary library = new();
    private TestNode phone = null!;
    private TestNode pc = null!;

    public async ValueTask InitializeAsync()
    {
        phone = await TestNode.StartAsync("Phone", services: s => s.AddSingleton<IMediaLibrary>(library));
        pc = await TestNode.StartAsync("PC");
        await phone.ConnectAsync(pc);
    }

    public async ValueTask DisposeAsync()
    {
        await phone.DisposeAsync();
        await pc.DisposeAsync();
        library.Dispose();
    }

    [Fact]
    public async Task What_is_taken_after_turning_it_on_lands_in_the_phones_folder()
    {
        await pc.Trust.SetAutoAcceptAsync(phone.Id, TrustStore.FollowSettingsLimit, Ct);
        library.Add("old.jpg", MediaKinds.CameraPhotos);
        var backup = phone.Get<PhotoBackupService>();

        backup.Enable(pc.Id, MediaKindNames.Default);
        library.Add("new.jpg", MediaKinds.CameraPhotos);
        library.Add("clip.mp4", MediaKinds.CameraVideos);
        library.Add("screen.png", MediaKinds.Screenshots);
        await backup.RunAsync(Ct);

        string root = pc.Get<SettingsService>().Current.DownloadDirectory;
        string month = Path.Combine(root, "Ảnh từ Phone", DateTimeOffset.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        await WaitUntil(() => File.Exists(Path.Combine(month, "new.jpg")) && File.Exists(Path.Combine(month, "clip.mp4")));
        await WaitUntil(() => !backup.Status.Waiting);
        Assert.False(File.Exists(Path.Combine(month, "old.jpg")));
        Assert.False(File.Exists(Path.Combine(month, "screen.png")));
        Assert.Equal(2, backup.Status.LastCount);
        Assert.Equal(library.LatestMarker() - 1, phone.Get<SettingsService>().User.PhotoBackupSent);
    }

    [Fact]
    public async Task A_declined_batch_is_not_sent_again()
    {
        var backup = phone.Get<PhotoBackupService>();
        backup.Enable(pc.Id, MediaKinds.CameraPhotos);
        library.Add("unwanted.jpg", MediaKinds.CameraPhotos);
        var inbox = pc.Get<InboxService>();
        await backup.RunAsync(Ct);
        await WaitUntil(() => inbox.GetOffers().Count == 1);
        Assert.Equal(["Ảnh từ Phone/"], Assert.Single(phone.Get<OfferService>().GetOffers()).Paths);

        await inbox.DeclineAsync(new OfferKey(phone.Id, inbox.GetOffers()[0].OfferId));

        // The decline reaches the phone, which settles the batch as answered.
        await WaitUntil(() => !backup.Status.Waiting);
        await backup.RunAsync(Ct);
        Assert.Single(phone.Get<OfferService>().GetOffers());
        Assert.Equal(library.LatestMarker(), phone.Get<SettingsService>().User.PhotoBackupSent);
    }

    [Fact]
    public async Task Nothing_goes_while_it_is_off_or_the_computer_is_away()
    {
        var backup = phone.Get<PhotoBackupService>();
        library.Add("private.jpg", MediaKinds.CameraPhotos);
        await backup.RunAsync(Ct);

        Assert.Empty(phone.Get<OfferService>().GetOffers());
        Assert.Null(backup.Status.Target);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>A photo library of small files in a temporary folder.</summary>
    private sealed class FakeLibrary : IMediaLibrary, IDisposable
    {
        private readonly string folder = Directory.CreateTempSubdirectory("shorekeeper-media-").FullName;
        private readonly List<(MediaItem Item, MediaKinds Kind)> items = [];

        public bool CanRead => true;

        public event EventHandler? Changed;

        public void Add(string name, MediaKinds kind)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, [.. Enumerable.Range(0, 1000).Select(i => (byte)i)]);
            lock (items)
            {
                items.Add((new MediaItem(items.Count + 1, path, name, 1000, DateTimeOffset.Now, File.GetLastWriteTimeUtc(path)), kind));
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public long LatestMarker()
        {
            lock (items)
            {
                return items.Count;
            }
        }

        public IReadOnlyList<MediaItem> After(long marker, MediaKinds kinds, int max)
        {
            lock (items)
            {
                return [.. items.Where(i => i.Item.Marker > marker && kinds.HasFlag(i.Kind)).Select(i => i.Item).Take(max)];
            }
        }

        public void Dispose() => Directory.Delete(folder, recursive: true);
    }
}
