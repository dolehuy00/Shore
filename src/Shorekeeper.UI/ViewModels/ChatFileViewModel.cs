using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Monitoring;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// Files sent between the two, shown among the messages (docs/15-chat.md §6.3): what, how big, how far along, [Tải] for
/// a new offer and [Xem] to see it in Hộp nhận or Đã gửi, where all it can do is.
/// </summary>
public sealed partial class ChatFileViewModel(string id, ChatViewModel owner) : ChatEntryViewModel(id)
{
    /// <summary>At most this many pictures of an offer show; the rest are "+N".</summary>
    private const int ShownPictures = 4;

    private OfferKey? key;
    private string? offerId;
    private string? picturesShown;
    private IReadOnlyList<string> locations = [];

    /// <summary>Until when "Thu hồi" is offered: never for theirs, any time while it waits to be delivered.</summary>
    private DateTimeOffset recallUntil;

    /// <summary>Pictures of an offer of pictures, once they can be read here (sent: the originals; received: once downloaded).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPictures), nameof(IsPlain))]
    public partial IReadOnlyList<ChatPicture> Pictures { get; set; } = [];

    public bool HasPictures => Pictures.Count > 0;

    /// <summary>A card with its icon and name (no pictures to show, not taken back).</summary>
    public bool IsPlain => !HasPictures && !IsRecalled;

    /// <summary>Taken back by its sender: only <see cref="RecalledText"/> shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlain))]
    public partial bool IsRecalled { get; set; }

    [ObservableProperty]
    public partial string RecalledText { get; set; } = "";

    /// <summary>"+3 ảnh" when more pictures came than show, "Thu gọn" while all of them show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    public partial string? MoreText { get; set; }

    public bool HasMore => MoreText is not null;

    /// <summary>All the pictures show, small, in place of the first few.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>All the pictures, small; loaded only while <see cref="IsExpanded"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChatPicture> AllPictures { get; set; } = [];

    /// <summary>"Trả lời" (docs/18-chat-extras.md §4): what is sent next answers this card.</summary>
    [RelayCommand]
    private void Reply() => owner.StartReply(this);

    /// <summary>"+N ảnh" / "Thu gọn". All of them load before the card grows, so it changes size once.</summary>
    [RelayCommand]
    private async Task ToggleMoreAsync()
    {
        if (IsExpanded)
        {
            IsExpanded = false;
            AllPictures = [];
        }
        else if (await LoadAsync(locations, picturesShown) is { } all)
        {
            AllPictures = all;
            IsExpanded = true;
        }

        ShowMoreText();
    }

    /// <summary>Ours and still open: it can be withdrawn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopSharing))]
    public partial bool CanWithdraw { get; set; }

    /// <summary>Ours, sent less than five minutes ago (or not delivered yet), not taken back: "Thu hồi".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopSharing))]
    public partial bool CanRecall { get; set; }

    /// <summary>"Ngừng chia sẻ" once it is too late to take it back.</summary>
    public bool CanStopSharing => CanWithdraw && !CanRecall;

    [RelayCommand]
    private Task WithdrawAsync() => offerId is { } id ? owner.WithdrawAsync(id) : Task.CompletedTask;

    [RelayCommand]
    private Task RecallAsync() => offerId is { } id ? owner.RecallFileAsync(id) : Task.CompletedTask;

    internal override void RefreshRecall() => CanRecall = DateTimeOffset.UtcNow <= recallUntil;

    [RelayCommand]
    private void OpenPicture(ChatPicture picture) => owner.OpenPicture(picture, received: !IsMine);

    /// <summary>Loads the pictures in the background when where they are has changed; none for a card of other files.</summary>
    internal void ShowPictures(IReadOnlyList<string> locations)
    {
        string shown = string.Join('\n', locations);
        if (shown == picturesShown)
        {
            return;
        }

        picturesShown = shown;
        this.locations = locations;
        if (locations.Count <= ShownPictures)
        {
            IsExpanded = false;
            AllPictures = [];
        }

        ShowMoreText();
        if (locations.Count == 0)
        {
            Pictures = [];
            return;
        }

        _ = ShowAsync();

        async Task ShowAsync()
        {
            if (await LoadAsync([.. locations.Take(ShownPictures)], shown) is { } first)
            {
                Pictures = first;
            }

            if (IsExpanded && await LoadAsync(locations, shown) is { } all && IsExpanded)
            {
                AllPictures = all;
            }
        }
    }

    private void ShowMoreText() =>
        MoreText = locations.Count <= ShownPictures ? null
            : IsExpanded ? L.T("Thu gọn")
            : L.F("+{0} ảnh", locations.Count - ShownPictures);

    /// <summary>The pictures, or null when where they are has changed meanwhile.</summary>
    private async Task<List<ChatPicture>?> LoadAsync(IReadOnlyList<string> locations, string? shown)
    {
        List<ChatPicture> loaded = await Task.Run(() =>
        {
            List<ChatPicture> pictures = [];
            foreach (string location in locations)
            {
                try
                {
                    using Stream stream = owner.OpenRead(location);
                    pictures.Add(new ChatPicture(Bitmap.DecodeToWidth(stream, 480), location));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    // Gone, or a kind the decoder does not know (HEIC): the card says what it is instead.
                }
            }

            return pictures;
        });
        return picturesShown == shown ? loaded : null;
    }

    /// <summary>Sent by us: on the right, like our messages.</summary>
    [ObservableProperty]
    public partial bool IsMine { get; set; }

    /// <summary>In a group's conversation: who sent it, above it, in their color.</summary>
    [ObservableProperty]
    public partial bool ShowsAuthor { get; set; }

    [ObservableProperty]
    public partial string AuthorName { get; set; } = "";

    [ObservableProperty]
    public partial IBrush? AuthorBrush { get; set; }

    internal void ShowAuthor(string name, DeviceId sender)
    {
        AuthorName = name;
        AuthorBrush = ChatBubbleViewModel.BrushOf(sender);
        ShowsAuthor = true;
    }

    /// <summary>"app.zip + 2 mục", or the words of "Gửi văn bản".</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    /// <summary>Text alone: shown whole here, its only place (docs/17-ui-cleanup.md §2), with [Sao chép] and [Mở] for a link.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiles))]
    public partial bool IsText { get; set; }

    public bool IsFiles => !IsText;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Text alone with no words (old data): "(Không có nội dung)", dimmed, nothing to copy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    public partial bool IsEmptyText { get; set; }

    public bool CanCopy => IsText && !IsEmptyText;

    [ObservableProperty]
    public partial bool IsLink { get; set; }

    [RelayCommand]
    private void Copy() => owner.CopyText?.Invoke(Text);

    [RelayCommand]
    private void OpenLink() => owner.OpenLink?.Invoke(Text);

    /// <summary>"12,4 MB · Đã nhận", "Bạn gửi · chưa mở".</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanDownload { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    public partial double Progress { get; set; } = -1;

    public bool IsDownloading => Progress >= 0;

    [RelayCommand]
    private void Download()
    {
        if (key is { } offer)
        {
            owner.Download(offer);
        }
    }

    [RelayCommand]
    private void Show() => owner.ShowFiles?.Invoke(!IsMine);

    /// <param name="peer">The person it is shown with; null in a group's conversation, where it went to several.</param>
    internal void Update(SentOffer offer, DeviceId? peer)
    {
        IsMine = true;
        key = null;
        offerId = offer.OfferId;
        CanWithdraw = offer.State == OfferState.Open && !offer.Recalled;
        SetContent(offer.Paths.Count == 0, offer.Note, offer.Paths);
        IReadOnlyList<SentRecipient> to = peer is { } one ? [.. offer.Recipients.Where(r => r.DeviceId == one)] : offer.Recipients;
        SentRecipient? recipient = to.Count > 0 ? to[0] : null;
        IsRecalled = offer.Recalled;
        bool reachedNobody = to.Count > 0 && to.All(r => r.State == RecipientState.Pending);
        RecalledText = ChatViewModel.RecalledText(offer.Paths.Count > 0 && offer.Paths.All(StorageReport.IsPicture), offer.Paths.Count == 0, reachedNobody);
        RecallText = ChatViewModel.RecallLabel(reachedNobody);
        recallUntil = offer.Recalled || to.Count == 0 ? DateTimeOffset.MinValue
            : reachedNobody ? DateTimeOffset.MaxValue
            : offer.CreatedAt + ChatService.RecallWindow;
        RefreshRecall();
        string state = offer.State switch
        {
            OfferState.Withdrawn => L.T("Đã thu hồi"),
            OfferState.Expired => L.T("Đã hết hạn"),
            _ when peer is null => GroupState(to),
            _ => recipient?.State switch
            {
                RecipientState.Pending => L.T("chờ gửi (đang offline)"),
                RecipientState.Delivered => L.T("chưa mở"),
                RecipientState.Downloading => L.T("đang tải"),
                RecipientState.Completed => L.T("đã nhận"),
                RecipientState.Partial => L.T("đã nhận một phần"),
                RecipientState.Declined => L.T("đã bỏ qua"),
                _ => L.T("lỗi"),
            },
        };
        StatusText = offer.Recalled ? Time(offer.CreatedAt) : Status(offer.CreatedAt, offer.TotalBytes, state);
        CanDownload = false;
        Progress = -1;
    }

    internal void Update(ReceivedOffer offer, bool senderOnline)
    {
        IsMine = false;
        key = new OfferKey(offer.SenderId, offer.OfferId);
        offerId = null;
        CanWithdraw = false;
        recallUntil = DateTimeOffset.MinValue;
        CanRecall = false;
        IsRecalled = offer.Recalled;
        RecalledText = ChatViewModel.RecalledText(
            offer.Files.Count > 0 && offer.Files.All(f => !f.IsDirectory && StorageReport.IsPicture(f.RelativePath)), offer.Files.Count == 0);
        SetContent(offer.Files.Count == 0, offer.Note, [.. offer.Files.Select(f => f.RelativePath)]);
        string state = offer.State switch
        {
            InboxState.New when offer.ExpiresAt <= DateTimeOffset.UtcNow => L.T("Đã hết hạn"),
            InboxState.New => senderOnline ? L.T("Mới") : L.F("Người gửi offline · {0}", TransferText.Remaining(offer.ExpiresAt - DateTimeOffset.UtcNow)),
            InboxState.Downloading => L.F("Đang tải {0:0}%", offer.TotalBytes == 0 ? 0 : 100.0 * offer.BytesReceived / offer.TotalBytes),
            InboxState.Completed => L.T("Đã nhận"),
            InboxState.Partial => L.T("Đã tải một phần"),
            InboxState.Failed => L.F("Lỗi: {0}", offer.Error),
            InboxState.Cancelled => L.T("Đã hủy"),
            InboxState.Declined => L.T("Đã bỏ qua"),
            InboxState.Withdrawn => L.T("Người gửi đã thu hồi"),
            _ => L.T("Đã hết hạn"),
        };
        StatusText = offer.Recalled ? Time(offer.SentAt) : Status(offer.SentAt, offer.TotalBytes, state);
        CanDownload = !offer.Recalled && offer.State == InboxState.New && senderOnline && offer.ExpiresAt > DateTimeOffset.UtcNow;
        Progress = offer.State == InboxState.Downloading && offer.TotalBytes > 0 ? 100.0 * offer.BytesReceived / offer.TotalBytes : -1;
    }

    private void SetContent(bool isText, string? note, IReadOnlyList<string> paths)
    {
        IsText = isText;
        IsEmptyText = isText && string.IsNullOrWhiteSpace(note);
        Text = IsEmptyText ? ChatViewModel.NoContent : isText ? note! : "";
        IsLink = !IsEmptyText && TransferText.LinkOf(Text) is not null;
        Title = isText ? TransferText.Preview(Text, 200) : TransferText.Summary(paths);
        OnPropertyChanged(nameof(CanCopy));
    }

    /// <summary>Ours to a group: how many have it ("2/3 đã nhận"), else how far it got.</summary>
    private static string GroupState(IReadOnlyList<SentRecipient> to)
    {
        int done = to.Count(r => r.State == RecipientState.Completed);
        return done > 0 ? L.F("{0}/{1} đã nhận", done, to.Count)
            : to.Any(r => r.State == RecipientState.Downloading) ? L.T("đang tải")
            : to.All(r => r.State == RecipientState.Pending) ? L.T("chờ gửi (đang offline)")
            : L.T("chưa mở");
    }

    /// <summary>"16:10 · 75 KB · đã nhận", the time as under a message.</summary>
    private string Status(DateTimeOffset at, long totalBytes, string state) =>
        IsText ? $"{Time(at)} · {state}" : $"{Time(at)} · {TransferText.Size(totalBytes)} · {state}";

    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", L.Culture);
}
