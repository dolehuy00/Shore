using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// "Đang chuyển" (docs/19-transfers-preview.md §3): every transfer in progress, received or sent, from the progress the
/// engine already keeps. One that finishes stays as "Xong" for <see cref="DoneFor"/>.
/// </summary>
public sealed partial class TransferActivityViewModel : ObservableObject
{
    /// <summary>How long a finished one stays (§6 Q3).</summary>
    internal static readonly TimeSpan DoneFor = TimeSpan.FromSeconds(5);

    private readonly InboxService inbox;
    private readonly OfferService offers;
    private readonly PeerNaming naming;
    private readonly Dictionary<string, TransferActivityItem> items = [];

    /// <summary>Bytes served to someone grow without an event: while anything moves, a look every second.</summary>
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TransferActivityViewModel(InboxService inbox, OfferService offers, PeerNaming naming)
    {
        this.inbox = inbox;
        this.offers = offers;
        this.naming = naming;
        timer.Tick += (_, _) => Refresh();
        inbox.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        offers.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>Oldest first.</summary>
    public ObservableCollection<TransferActivityItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>"⬇ 2 · ⬆ 1".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>All moving together: "12 MB/s".</summary>
    [ObservableProperty]
    public partial string SpeedText { get; set; } = "";

    /// <summary>Of everything still moving, 0 to 100.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>"Đang nhận 2 · đang gửi 1", or empty: for the tray and the phone's ongoing notification.</summary>
    [ObservableProperty]
    public partial string Describe { get; set; } = "";

    /// <summary>Opens a received file where the app can (desktop); null where it cannot, and there is no [Mở].</summary>
    public Action<string>? OpenFile { get; set; }

    /// <summary>A row was clicked: its offer in File, on the right tab.</summary>
    public event EventHandler<TransferActivityItem>? RevealRequested;

    internal void Reveal(TransferActivityItem item) => RevealRequested?.Invoke(this, item);

    internal void Cancel(TransferActivityItem item)
    {
        if (item.Received is { } key)
        {
            inbox.Cancel(key);
        }
    }

    internal void Open(TransferActivityItem item)
    {
        if (item.OpenPath is { } path)
        {
            OpenFile?.Invoke(path);
        }
    }

    private void Refresh() => Refresh(DateTimeOffset.UtcNow);

    internal void Refresh(DateTimeOffset now)
    {
        HashSet<string> moving = [];
        foreach (ReceivedOffer offer in inbox.GetOffers().Where(o => o.State == InboxState.Downloading))
        {
            var key = new OfferKey(offer.SenderId, offer.OfferId);
            TransferActivityItem item = Item(
                $"in:{offer.SenderId.Value}:{offer.OfferId}",
                () => new TransferActivityItem(this, key, null, null, TransferText.Summary(offer.Files.Select(f => f.RelativePath)), naming.Of(offer.SenderId)));
            item.Update(offer.BytesReceived, offer.TotalBytes, offer.ReconnectingUntil is not null, now);
            moving.Add(item.Id);
        }

        foreach (SentOffer offer in offers.GetOffers().Where(o => o.State == OfferState.Open))
        {
            foreach (SentRecipient recipient in offer.Recipients.Where(r => r.State == RecipientState.Downloading))
            {
                TransferActivityItem item = Item(
                    $"out:{offer.OfferId}:{recipient.DeviceId.Value}",
                    () => new TransferActivityItem(this, null, offer.OfferId, recipient.DeviceId, TransferText.Summary(offer.Paths), naming.Of(recipient.DeviceId)));
                item.Update(recipient.BytesServed, offer.TotalBytes, reconnecting: false, now);
                moving.Add(item.Id);
            }
        }

        foreach (TransferActivityItem item in items.Values.Where(i => !moving.Contains(i.Id)).ToList())
        {
            if (item.FinishedAt is null && Arrived(item, out string? path))
            {
                item.Finish(now, path);
            }
            else if (item.FinishedAt is not { } at || now - at >= DoneFor)
            {
                items.Remove(item.Id);
                Items.Remove(item);
            }
        }

        TransferActivityItem[] active = [.. Items.Where(i => i.FinishedAt is null)];
        long total = active.Sum(i => i.Total);
        Progress = total == 0 ? (Items.Count > 0 ? 100 : 0) : 100.0 * active.Sum(i => i.Done) / total;
        int incoming = Items.Count(i => i.IsIncoming);
        int outgoing = Items.Count - incoming;
        Summary = string.Join(" · ", new[] { incoming > 0 ? $"⬇ {incoming}" : null, outgoing > 0 ? $"⬆ {outgoing}" : null }.OfType<string>());
        double speed = active.Sum(i => i.Speed);
        SpeedText = speed > 0 ? L.F("{0}/s", TransferText.Size((long)speed)) : "";
        Describe = DescribeOf(active.Count(i => i.IsIncoming), active.Count(i => !i.IsIncoming));
        OnPropertyChanged(nameof(HasItems));
        timer.IsEnabled = Items.Count > 0;
    }

    /// <summary>"Đang nhận 2 · đang gửi 1".</summary>
    internal static string DescribeOf(int receiving, int sending) =>
        receiving > 0 && sending > 0 ? L.F("Đang nhận {0} · đang gửi {1}", receiving, sending)
        : receiving > 0 ? L.F("Đang nhận {0}", receiving)
        : sending > 0 ? L.F("Đang gửi {0}", sending)
        : "";

    private TransferActivityItem Item(string id, Func<TransferActivityItem> create)
    {
        if (!items.TryGetValue(id, out TransferActivityItem? item))
        {
            item = create();
            item.Id = id;
            items[id] = item;
            Items.Add(item);
        }

        return item;
    }

    /// <summary>Stopped moving because it arrived, rather than cancelled or failed; for one received, what to open.</summary>
    private bool Arrived(TransferActivityItem item, out string? path)
    {
        path = null;
        if (item.Received is { } key)
        {
            if (inbox.Get(key) is not { State: InboxState.Completed or InboxState.Partial } offer)
            {
                return false;
            }

            string[] paths = [.. offer.Files.Where(f => f is { State: InboxFileState.Completed, FinalPath: not null, IsDirectory: false }).Select(f => f.FinalPath!)];
            path = paths.Length == 1 ? paths[0] : paths.Length > 0 ? Path.GetDirectoryName(paths[0]) : null;
            return true;
        }

        return offers.GetOffers().FirstOrDefault(o => o.OfferId == item.SentOfferId)?.Recipients
            .FirstOrDefault(r => r.DeviceId == item.Recipient)?.State is RecipientState.Completed or RecipientState.Partial;
    }
}

/// <summary>A row of "Đang chuyển": an offer coming in, or someone taking one we sent.</summary>
public sealed partial class TransferActivityItem : ObservableObject
{
    private readonly TransferActivityViewModel owner;
    private long lastBytes;
    private DateTimeOffset lastAt;

    internal TransferActivityItem(TransferActivityViewModel owner, OfferKey? received, string? sentOfferId, DeviceId? recipient, string title, string person)
    {
        this.owner = owner;
        Received = received;
        SentOfferId = sentOfferId;
        Recipient = recipient;
        Title = title;
        Person = person;
    }

    internal string Id { get; set; } = "";

    /// <summary>Coming in: the offer in File › Nhận.</summary>
    public OfferKey? Received { get; }

    /// <summary>Going out: the offer in File › Đã gửi, and who takes it.</summary>
    public string? SentOfferId { get; }

    public DeviceId? Recipient { get; }

    public bool IsIncoming => Received is not null;

    public string Arrow => IsIncoming ? "⬇" : "⬆";

    public string Title { get; }

    /// <summary>Who sends it, or who takes it.</summary>
    public string Person { get; }

    internal long Done { get; private set; }

    internal long Total { get; private set; }

    /// <summary>Bytes a second, smoothed.</summary>
    internal double Speed { get; private set; }

    internal DateTimeOffset? FinishedAt { get; private set; }

    internal string? OpenPath { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>"45% · 98 MB/s · còn 2 phút", "Đang nối lại…" or "Xong ✓".</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel), nameof(CanOpen))]
    public partial bool IsDone { get; set; }

    public bool CanCancel => IsIncoming && !IsDone;

    public bool CanOpen => IsDone && OpenPath is not null && owner.OpenFile is not null;

    internal void Update(long done, long total, bool reconnecting, DateTimeOffset now)
    {
        Speed = NextSpeed(Speed, lastBytes, lastAt, done, now, out bool sampled);
        if (sampled || done < lastBytes || lastAt == default)
        {
            (lastBytes, lastAt) = (done, now);
        }

        (Done, Total) = (done, total);
        Progress = total == 0 ? 0 : Math.Min(100, 100.0 * done / total);
        Status = StatusOf(Progress, Speed, total - done, reconnecting);
    }

    internal void Finish(DateTimeOffset now, string? path)
    {
        FinishedAt = now;
        OpenPath = path;
        Progress = 100;
        Status = L.T("Xong ✓");
        IsDone = true;
    }

    /// <summary>
    /// The speed after <paramref name="bytes"/> at <paramref name="now"/>: sampled every half second at least and smoothed
    /// as the File page does; a count going back (a retry) starts over.
    /// </summary>
    internal static double NextSpeed(double speed, long lastBytes, DateTimeOffset lastAt, long bytes, DateTimeOffset now, out bool sampled)
    {
        sampled = false;
        if (lastAt == default || bytes < lastBytes)
        {
            return 0;
        }

        double seconds = (now - lastAt).TotalSeconds;
        if (seconds < 0.5)
        {
            return speed;
        }

        sampled = true;
        double current = (bytes - lastBytes) / seconds;
        return speed == 0 ? current : (0.7 * speed) + (0.3 * current);
    }

    /// <summary>"45% · 98 MB/s · còn 2 phút"; the time left only once there is a speed to go by.</summary>
    internal static string StatusOf(double progress, double speed, long left, bool reconnecting)
    {
        if (reconnecting)
        {
            return L.T("Đang nối lại…");
        }

        string percent = $"{Math.Min(99, Math.Floor(progress)):0}%";
        if (speed <= 0)
        {
            return percent;
        }

        TimeSpan time = TimeSpan.FromSeconds(left / speed);
        string remaining = time.TotalHours >= 1 ? L.F("còn {0} giờ", (int)Math.Ceiling(time.TotalHours))
            : time.TotalMinutes >= 1 ? L.F("còn {0} phút", (int)Math.Ceiling(time.TotalMinutes))
            : L.T("còn vài giây");
        return $"{percent} · {L.F("{0}/s", TransferText.Size((long)speed))} · {remaining}";
    }

    [RelayCommand]
    private void Cancel() => owner.Cancel(this);

    [RelayCommand]
    private void Open() => owner.Open(this);

    [RelayCommand]
    private void Reveal() => owner.Reveal(this);
}
