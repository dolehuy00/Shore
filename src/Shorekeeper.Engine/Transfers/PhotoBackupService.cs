using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Transfers;

/// <summary>What "Tự gửi ảnh" shows about itself.</summary>
/// <param name="Waiting">The batch on its way has not been taken yet.</param>
/// <param name="LastCount">Items in the last batch taken, with <paramref name="LastAt"/>; 0 when none yet.</param>
public sealed record PhotoBackupStatus(DeviceId? Target, MediaKinds Kinds, bool Waiting, int LastCount, DateTimeOffset? LastAt);

/// <summary>
/// "Tự gửi ảnh" (docs/06-file-transfer.md §11): a phone sends the pictures and videos taken since it was turned on to one
/// computer, a batch at a time, whenever that computer is online. A batch counts as sent once the computer has taken
/// it, or declined it (the user said no); failed or expired, it is offered again five minutes later. Files go under "Ảnh từ &lt;phone&gt;/&lt;yyyy-MM&gt;/", so a
/// computer that accepts this phone's files by itself ("Tự nhận file") files them there.
/// </summary>
public sealed class PhotoBackupService : IHostedService, IDisposable
{
    public const int MaxBatchFiles = 200;
    public const long MaxBatchBytes = 1L << 30;

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AfterChange = TimeSpan.FromSeconds(10);

    private readonly SettingsService settings;
    private readonly OfferService offers;
    private readonly PeerDirectory directory;
    private readonly LocalDevice device;
    private readonly ILogger<PhotoBackupService> logger;
    private readonly IMediaLibrary? library;
    private readonly TimeProvider timeProvider;
    private readonly ITimer timer;
    private readonly SemaphoreSlim running = new(1, 1);
    private volatile bool again;
    private DateTimeOffset notBefore;
    private int offeredCount;
    private int lastCount;
    private DateTimeOffset? lastAt;

    public PhotoBackupService(
        SettingsService settings,
        OfferService offers,
        PeerDirectory directory,
        LocalDevice device,
        TimeProvider timeProvider,
        ILogger<PhotoBackupService> logger,
        IMediaLibrary? library = null)
    {
        this.settings = settings;
        this.offers = offers;
        this.directory = directory;
        this.device = device;
        this.logger = logger;
        this.library = library;
        this.timeProvider = timeProvider;
        timer = timeProvider.CreateTimer(_ => _ = RunAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Enabled, sent or taken. Raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>This device has a photo library to send from (a phone).</summary>
    public bool IsAvailable => library is not null;

    public PhotoBackupStatus Status
    {
        get
        {
            ShorekeeperSettings user = settings.User;
            DeviceId? target = DeviceId.TryParse(user.PhotoBackupTarget, out DeviceId id) ? id : null;
            return new PhotoBackupStatus(target, MediaKindNames.Parse(user.PhotoBackupKinds), user.PhotoBackupOffer is not null, lastCount, lastAt);
        }
    }

    /// <summary>Sends to <paramref name="target"/> what is taken from now on.</summary>
    public void Enable(DeviceId target, MediaKinds kinds)
    {
        long latest = library?.LatestMarker() ?? 0;
        settings.Update(s => s with
        {
            PhotoBackupTarget = target.Value,
            PhotoBackupKinds = MediaKindNames.Format(kinds),
            PhotoBackupSent = latest,
            PhotoBackupOffer = null,
            PhotoBackupOfferUpTo = null,
        });
        logger.LogInformation("Photo backup to {Target} on, after {Marker}", target, latest);
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RunAsync();
    }

    public void SetKinds(MediaKinds kinds)
    {
        settings.Update(s => s with { PhotoBackupKinds = MediaKindNames.Format(kinds) });
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RunAsync();
    }

    /// <summary>Stops sending; a batch on its way stays offered.</summary>
    public void Disable()
    {
        settings.Update(s => s with { PhotoBackupTarget = null, PhotoBackupOffer = null, PhotoBackupOfferUpTo = null });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (library is null)
        {
            return Task.CompletedTask;
        }

        library.Changed += OnLibraryChanged;
        directory.Changed += OnPeerChanged;
        offers.Changed += OnOfferChanged;
        timer.Change(AfterChange, Every);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (library is not null)
        {
            library.Changed -= OnLibraryChanged;
            directory.Changed -= OnPeerChanged;
            offers.Changed -= OnOfferChanged;
        }

        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        timer.Dispose();
        running.Dispose();
    }

    /// <summary>Checks the batch on its way, then sends the next one if the computer is online. Runs one at a time.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (library is not { CanRead: true })
        {
            return;
        }

        if (!await running.WaitAsync(0, cancellationToken))
        {
            again = true;
            return;
        }

        try
        {
            do
            {
                again = false;
                await RunOnceAsync(library, cancellationToken);
            }
            while (again);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Photo backup failed");
        }
        finally
        {
            running.Release();
        }
    }

    private async Task RunOnceAsync(IMediaLibrary media, CancellationToken cancellationToken)
    {
        ShorekeeperSettings user = settings.User;
        if (!DeviceId.TryParse(user.PhotoBackupTarget, out DeviceId target))
        {
            return;
        }

        if (user.PhotoBackupOffer is { } pending && !Settle(pending, target, user.PhotoBackupOfferUpTo))
        {
            return;
        }

        if (timeProvider.GetUtcNow() < notBefore)
        {
            return;
        }

        if (!directory.Snapshot().Any(p => p.DeviceId == target && p.State == PeerState.Online))
        {
            return;
        }

        List<MediaItem> batch = [];
        long bytes = 0;
        foreach (MediaItem item in media.After(settings.User.PhotoBackupSent ?? media.LatestMarker(), MediaKindNames.Parse(user.PhotoBackupKinds), MaxBatchFiles))
        {
            if (batch.Count > 0 && bytes + item.Size > MaxBatchBytes)
            {
                break;
            }

            batch.Add(item);
            bytes += item.Size;
        }

        if (batch.Count == 0)
        {
            return;
        }

        string folder = SafeName(L.F("Ảnh từ {0}", device.DisplayName));
        // The folder itself too, so "Đã gửi" shows it.
        PreparedFile[] files =
        [
            new($"{folder}/", "", 0, batch[^1].AddedAt, IsDirectory: true, IsLocked: false),
            .. batch.Select(i => new PreparedFile($"{folder}/{i.AddedAt.ToLocalTime():yyyy-MM}/{i.Name}", i.Location, i.Size, OfferService.ToMsPrecision(i.ModifiedAt.UtcDateTime), IsDirectory: false, IsLocked: false)),
        ];
        string offerId = await offers.CreateAsync(new PreparedOffer(files, []), [target], null, snapshotLocked: false, groupId: null, cancellationToken);
        long upTo = batch.Max(i => i.Marker);
        offeredCount = batch.Count;
        settings.Update(s => s with { PhotoBackupOffer = offerId, PhotoBackupOfferUpTo = upTo });
        logger.LogInformation("Photo backup: {Count} items ({Bytes} bytes) offered to {Target}", batch.Count, bytes, target);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Settles the batch on its way.</summary>
    /// <returns>Whether the next batch may go: taken (then it counts as sent), or gone and to be offered again.</returns>
    private bool Settle(string offerId, DeviceId target, long? upTo)
    {
        SentOffer? offer = offers.GetOffers().FirstOrDefault(o => o.OfferId == offerId);
        RecipientState? state = offer?.Recipients.FirstOrDefault(r => r.DeviceId == target)?.State;
        if (state is RecipientState.Completed or RecipientState.Partial or RecipientState.Declined)
        {
            if (state != RecipientState.Declined)
            {
                lastCount = offeredCount;
                lastAt = timeProvider.GetLocalNow();
            }

            settings.Update(s => s with { PhotoBackupSent = upTo ?? s.PhotoBackupSent, PhotoBackupOffer = null, PhotoBackupOfferUpTo = null });
            logger.LogInformation("Photo backup: batch {Offer} {State} by {Target}", offerId, state, target);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (offer is { State: OfferState.Open } && state is RecipientState.Pending or RecipientState.Delivered or RecipientState.Downloading)
        {
            return false;
        }

        // Failed, expired or forgotten: the same items go again, not at once (a full disk or a deleted picture would fail again).
        notBefore = timeProvider.GetUtcNow() + RetryAfter;
        settings.Update(s => s with { PhotoBackupOffer = null, PhotoBackupOfferUpTo = null });
        logger.LogInformation("Photo backup: batch {Offer} was not taken ({State}); offering it again in {Delay}", offerId, state, RetryAfter);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>A folder name a computer accepts: the phone's name may hold any character.</summary>
    private static string SafeName(string name) =>
        new string([.. name.Select(c => c < 0x20 || "<>:\"\\|?*/".Contains(c) ? '_' : c)]).Trim().TrimEnd('.');

    private void OnLibraryChanged(object? sender, EventArgs e) => timer.Change(AfterChange, Every);

    private void OnPeerChanged(object? sender, PeerChange change)
    {
        if (change.Peer.State == PeerState.Online && change.Peer.DeviceId.Value == settings.User.PhotoBackupTarget)
        {
            _ = RunAsync();
        }
    }

    private void OnOfferChanged(object? sender, string offerId)
    {
        if (offerId == settings.User.PhotoBackupOffer)
        {
            _ = RunAsync();
        }
    }
}
