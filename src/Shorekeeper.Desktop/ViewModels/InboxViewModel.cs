using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    }

    private void Refresh(OfferKey key)
    {
        if (inbox.Get(key) is { } offer)
        {
            Apply(offer);
            NewCount = Items.Count(i => i.IsNew);
            OnPropertyChanged(nameof(IsEmpty));
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
    private ReceivedOffer? offer;
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
    public partial string DownloadText { get; set; } = "Tải";

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
        Summary = $"{TransferText.Summary(value.Files.Select(f => f.RelativePath))} · {TransferText.Size(value.TotalBytes)}";
        Note = value.Note is null ? null : $"\"{value.Note}\"";
        OnPropertyChanged(nameof(HasNote));
        IsNew = value.State == InboxState.New && open;
        IsDownloading = value.State == InboxState.Downloading;
        CanDownload = open && senderOnline && value.State is InboxState.New or InboxState.Partial or InboxState.Failed or InboxState.Cancelled;
        CanPick = CanDownload && value.Files.Count(f => !f.IsDirectory && f.State != InboxFileState.Completed) > 1;
        DownloadText = value.State switch
        {
            InboxState.New => "Tải",
            InboxState.Partial => "Tải phần còn lại",
            _ => "Tải lại",
        };
        CanOpen = value.Files.Any(f => f.FinalPath is not null);
        IsError = value.State == InboxState.Failed;
        Progress = value.TotalBytes == 0 ? 0 : 100.0 * value.BytesReceived / value.TotalBytes;
        UpdateSpeed(value.BytesReceived, now);

        StatusText = value.State switch
        {
            InboxState.Downloading when value.ReconnectingUntil is { } until =>
                $"Mất kết nối với {SenderName}, đang thử lại (còn {Math.Max(0, (int)(until - now).TotalSeconds)} giây)…",
            InboxState.Downloading => $"Đang tải {Progress:0}% · {TransferText.Size((long)speed)}/s",
            InboxState.Completed => "Đã nhận",
            InboxState.Partial => $"Đã nhận {value.Files.Count(f => !f.IsDirectory && f.State == InboxFileState.Completed)}/{value.Files.Count(f => !f.IsDirectory)} file",
            InboxState.Failed => $"Lỗi: {value.Error}",
            InboxState.Cancelled => "Đã hủy",
            InboxState.Declined => "Đã bỏ qua",
            InboxState.Withdrawn => "Người gửi đã thu hồi",
            InboxState.Expired => "Đã hết hạn",
            _ when !open => "Đã hết hạn",
            _ when !senderOnline => $"Người gửi offline · {TransferText.Remaining(value.ExpiresAt - now)}",
            _ => $"Mới · {TransferText.Remaining(value.ExpiresAt - now)}",
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

    /// <summary>Opens the received file, or the folder for several files.</summary>
    [RelayCommand]
    private void Open()
    {
        string[] received = [.. offer?.Files.Where(f => f.FinalPath is not null && !f.IsDirectory).Select(f => f.FinalPath!) ?? []];
        if (received.Length == 1)
        {
            Process.Start(new ProcessStartInfo(received[0]) { UseShellExecute = true });
        }
        else
        {
            OpenFolder();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        string? first = offer?.Files.FirstOrDefault(f => f.FinalPath is not null)?.FinalPath;
        if (first is not null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true });
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
