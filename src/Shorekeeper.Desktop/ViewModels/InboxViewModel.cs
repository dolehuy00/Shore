using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>"Hộp nhận" (docs/10-ux.md §4): offers sent to us; the user decides whether to download.</summary>
public sealed partial class InboxViewModel : ObservableObject
{
    private readonly InboxService inbox;
    private readonly PeerNames names;

    public InboxViewModel(InboxService inbox, PeerNames names, PeerDirectory directory, TimeProvider timeProvider)
    {
        this.inbox = inbox;
        this.names = names;
        TimeProvider = timeProvider;
        inbox.Changed += (_, key) => Dispatcher.UIThread.Post(() => Refresh(key));
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshAll);

        // Countdowns ("còn 42 giây", "còn 23 giờ") move on even without events.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshAll();
        timer.Start();
        RefreshAll();
    }

    public TimeProvider TimeProvider { get; }

    public ObservableCollection<InboxItemViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    /// <summary>"Mới nhận" on the Xóm page: the latest offers of the last day.</summary>
    public ObservableCollection<InboxItemViewModel> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    private const int RecentCount = 3;

    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(1);

    /// <summary>Offers waiting for a decision; shown as a badge in the navigation.</summary>
    [ObservableProperty]
    public partial int NewCount { get; set; }

    internal InboxService Inbox => inbox;

    internal PeerNames Names => names;

    private void RefreshAll()
    {
        foreach (ReceivedOffer offer in inbox.GetOffers())
        {
            Apply(offer);
        }

        NewCount = Items.Count(i => i.IsNew);
        OnPropertyChanged(nameof(IsEmpty));
        UpdateRecent();
    }

    private void Refresh(OfferKey key)
    {
        if (inbox.Get(key) is { } offer)
        {
            Apply(offer);
            NewCount = Items.Count(i => i.IsNew);
            OnPropertyChanged(nameof(IsEmpty));
            UpdateRecent();
        }
    }

    private void UpdateRecent()
    {
        DateTimeOffset since = TimeProvider.GetUtcNow() - RecentWindow;
        InboxItemViewModel[] latest = [.. Items.Where(i => i.ReceivedAt >= since).Take(RecentCount)];
        if (!latest.SequenceEqual(Recent))
        {
            Recent.Clear();
            foreach (InboxItemViewModel item in latest)
            {
                Recent.Add(item);
            }

            OnPropertyChanged(nameof(HasRecent));
        }
    }

    private void Apply(ReceivedOffer offer)
    {
        var key = new OfferKey(offer.SenderId, offer.OfferId);
        InboxItemViewModel? item = Items.FirstOrDefault(i => i.Key == key);
        if (item is null)
        {
            item = new InboxItemViewModel(key, this);
            // Newest first.
            int index = 0;
            while (index < Items.Count && Items[index].ReceivedAt > offer.ReceivedAt)
            {
                index++;
            }

            Items.Insert(index, item);
        }

        item.Update(offer);
    }
}

public sealed partial class InboxItemViewModel(OfferKey key, InboxViewModel owner) : ObservableObject
{
    private const int ThumbnailWidth = 112;

    private ReceivedOffer? offer;

    /// <summary>Everything in the offer, for "Xem cả N mục".</summary>
    public OfferContents Contents { get; } = new([]);
    private string? thumbnailPath;
    private long lastBytes;
    private DateTimeOffset lastAt;
    private double speed;

    public OfferKey Key { get; } = key;

    public DateTimeOffset ReceivedAt => offer?.ReceivedAt ?? default;

    [ObservableProperty]
    public partial string SenderName { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string? Note { get; set; }

    /// <summary>The file names alone ("app.zip + 2 mục").</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    /// <summary>"6 file · 2,7 MB".</summary>
    [ObservableProperty]
    public partial string Details { get; set; } = "";

    [ObservableProperty]
    public partial string Ago { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    public partial TransferKind Kind { get; set; }

    /// <summary>The first received picture, small.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    public partial Bitmap? Thumbnail { get; set; }

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>Every file and folder of the offer (the title shows only the first).</summary>
    public IEnumerable<string> FileNames => offer?.Files.Select(f => f.RelativePath) ?? [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial bool IsNew { get; set; }

    [ObservableProperty]
    public partial bool CanDownload { get; set; }

    [ObservableProperty]
    public partial string DownloadText { get; set; } = L.T("Tải");

    /// <summary>"Chọn file…" makes sense when several files are still to come.</summary>
    [ObservableProperty]
    public partial bool CanPick { get; set; }

    [ObservableProperty]
    public partial bool CanOpen { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    public bool HasNote => Note is not null;

    public void Update(ReceivedOffer value)
    {
        offer = value;
        DateTimeOffset now = owner.TimeProvider.GetUtcNow();
        bool senderOnline = owner.Names.IsOnline(value.SenderId);
        bool open = value.ExpiresAt > now;

        SenderName = owner.Names.Of(value.SenderId);
        Title = TransferText.Summary(value.Files.Select(f => f.RelativePath));
        Contents.Update([.. value.Files.Select(f => f.RelativePath)]);
        Summary = $"{Title} · {TransferText.Size(value.TotalBytes)}";
        Details = $"{TransferText.FileCount(value.Files.Count(f => !f.IsDirectory))} · {TransferText.Size(value.TotalBytes)}";
        Ago = TransferText.Ago(value.ReceivedAt, now);
        Kind = TransferText.KindOf([.. value.Files.Where(f => !f.IsDirectory).Select(f => f.RelativePath)]);
        IsCompleted = value.State == InboxState.Completed;
        LoadThumbnail(value);
        Note = value.Note is null ? null : $"\"{value.Note}\"";
        OnPropertyChanged(nameof(HasNote));
        IsNew = value.State == InboxState.New && open;
        IsDownloading = value.State == InboxState.Downloading;
        CanDownload = open && senderOnline && value.State is InboxState.New or InboxState.Partial or InboxState.Failed or InboxState.Cancelled;
        CanPick = CanDownload && value.Files.Count(f => !f.IsDirectory && f.State != InboxFileState.Completed) > 1;
        DownloadText = value.State switch
        {
            InboxState.New => L.T("Tải"),
            InboxState.Partial => L.T("Tải phần còn lại"),
            _ => L.T("Tải lại"),
        };
        CanOpen = value.Files.Any(f => f.FinalPath is not null);
        IsError = value.State == InboxState.Failed;
        Progress = value.TotalBytes == 0 ? 0 : 100.0 * value.BytesReceived / value.TotalBytes;
        UpdateSpeed(value.BytesReceived, now);

        StatusText = value.State switch
        {
            InboxState.Downloading when value.ReconnectingUntil is { } until =>
                L.F("Mất kết nối với {0}, đang thử lại (còn {1} giây)…", SenderName, Math.Max(0, (int)(until - now).TotalSeconds)),
            InboxState.Downloading => L.F("Đang tải {0:0}% · {1}/s", Progress, TransferText.Size((long)speed)),
            InboxState.Completed => L.T("Đã nhận"),
            InboxState.Partial => L.F("Đã nhận {0}/{1} file", value.Files.Count(f => !f.IsDirectory && f.State == InboxFileState.Completed), value.Files.Count(f => !f.IsDirectory)),
            InboxState.Failed => L.F("Lỗi: {0}", value.Error),
            InboxState.Cancelled => L.T("Đã hủy"),
            InboxState.Declined => L.T("Đã bỏ qua"),
            InboxState.Withdrawn => L.T("Người gửi đã thu hồi"),
            InboxState.Expired => L.T("Đã hết hạn"),
            _ when !open => L.T("Đã hết hạn"),
            _ when !senderOnline => L.F("Người gửi offline · {0}", TransferText.Remaining(value.ExpiresAt - now)),
            _ => L.F("Mới · {0}", TransferText.Remaining(value.ExpiresAt - now)),
        };
    }

    [RelayCommand]
    private void Download() => owner.Inbox.Download(Key);

    [RelayCommand]
    private async Task PickFilesAsync()
    {
        if (offer is not null && await DialogService.PickOfferFilesAsync(offer) is { } picked)
        {
            owner.Inbox.Download(Key, [.. picked]);
        }
    }

    [RelayCommand]
    private Task DeclineAsync() => owner.Inbox.DeclineAsync(Key);

    [RelayCommand]
    private void Cancel() => owner.Inbox.Cancel(Key);

    /// <summary>Decodes the first received picture once, off the UI thread.</summary>
    private void LoadThumbnail(ReceivedOffer value)
    {
        string? path = value.Files.FirstOrDefault(f => f.FinalPath is not null && !f.IsDirectory && TransferText.IsImage(f.FinalPath))?.FinalPath;
        if (path is null || path == thumbnailPath)
        {
            return;
        }

        thumbnailPath = path;
        _ = Task.Run(() =>
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, ThumbnailWidth, BitmapInterpolationMode.MediumQuality);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // Moved, deleted or not really a picture: keep the icon.
                return null;
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() => Thumbnail = t.Result), TaskScheduler.Default);
    }

    /// <summary>Opens the received file, or the folder for several files.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        string[] received = [.. offer?.Files.Where(f => f.FinalPath is not null && !f.IsDirectory).Select(f => f.FinalPath!) ?? []];
        if (received.Length != 1)
        {
            await OpenFolderAsync();
        }
        else if (!File.Exists(received[0]))
        {
            await ShowMissingAsync();
        }
        else
        {
            await StartAsync(new ProcessStartInfo(received[0]) { UseShellExecute = true });
        }
    }

    /// <summary>Shows the received files in Explorer: the first one still there, else the folder they went to.</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        string[] received = [.. offer?.Files.Where(f => f.FinalPath is not null).Select(f => f.FinalPath!) ?? []];
        if (received.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p)) is { } existing)
        {
            await StartAsync(new ProcessStartInfo("explorer.exe", $"/select,\"{existing}\"") { UseShellExecute = true });
        }
        else if (received.Select(Path.GetDirectoryName).FirstOrDefault(Directory.Exists) is { } folder)
        {
            await StartAsync(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        else
        {
            await ShowMissingAsync();
        }
    }

    private static async Task ShowMissingAsync() =>
        await DialogService.ChooseAsync(L.T("Mở"), L.T("Không còn thấy file đã nhận: có thể nó đã bị xóa hoặc chuyển đi chỗ khác."), [L.T("Đóng")]);

    /// <summary>Opening can still fail (deleted just now, blocked by policy); that must not take the app down.</summary>
    private static async Task StartAsync(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            await DialogService.ChooseAsync(L.T("Mở"), L.F("Không mở được: {0}", ex.Message), [L.T("Đóng")]);
        }
    }

    /// <summary>Smoothed transfer speed for the status line.</summary>
    private void UpdateSpeed(long bytes, DateTimeOffset now)
    {
        if (!IsDownloading || bytes < lastBytes)
        {
            (lastBytes, lastAt, speed) = (bytes, now, 0);
            return;
        }

        double seconds = (now - lastAt).TotalSeconds;
        if (seconds >= 0.5)
        {
            double current = (bytes - lastBytes) / seconds;
            speed = speed == 0 ? current : (0.7 * speed) + (0.3 * current);
            (lastBytes, lastAt) = (bytes, now);
        }
    }
}
