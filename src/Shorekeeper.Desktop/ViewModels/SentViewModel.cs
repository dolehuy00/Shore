using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>"Đã gửi" (docs/10-ux.md §3): our offers and where each recipient is at.</summary>
public sealed partial class SentViewModel : ObservableObject
{
    private readonly OfferService offers;

    public SentViewModel(OfferService offers, PeerNames names, PeerDirectory directory, TimeProvider timeProvider)
    {
        this.offers = offers;
        Names = names;
        TimeProvider = timeProvider;
        offers.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
    }

    public ObservableCollection<SentItemViewModel> Items { get; } = [];

    /// <summary>There are offers, but the filter or the search keeps none.</summary>
    public bool HasNoMatch => Items.Count > 0 && !Items.Any(i => i.IsShown);

    private FileFilter filter;
    private string query = "";

    /// <summary>The Files page's filter and search box (docs/17-ui-cleanup.md §4).</summary>
    internal void SetView(FileFilter value, string text)
    {
        filter = value;
        query = text;
        UpdateView();
    }

    private void UpdateView()
    {
        TransferText.ApplyView(Items, filter, query, TimeProvider.GetUtcNow());
        OnPropertyChanged(nameof(HasNoMatch));
        SelectionChanged();
    }

    internal PeerNames Names { get; }

    internal TimeProvider TimeProvider { get; }

    internal OfferService Offers => offers;

    // ───────────── "Xóa khỏi lịch sử" (docs/10-ux.md §4) ─────────────

    /// <summary>"Chọn nhiều" (⋮ on an offer): a check box on each offer, and the actions for the checked ones.</summary>
    [ObservableProperty]
    public partial bool IsSelecting { get; set; }

    public int SelectedCount => Items.Count(i => i.IsSelected);

    private int ShownCount => Items.Count(i => i.IsShown);

    public bool HasSelection => SelectedCount > 0;

    public string SelectedText => L.F("Đã chọn {0}", SelectedCount);

    /// <summary>"Chọn tất cả": checked when all are, empty when none, in between otherwise.</summary>
    public bool? AllSelected
    {
        get => SelectedCount == 0 ? false : SelectedCount == ShownCount ? true : null;
        set
        {
            // Only what the filter shows: nothing hidden gets deleted by surprise.
            bool select = SelectedCount < ShownCount;
            foreach (SentItemViewModel item in Items.Where(i => i.IsShown))
            {
                item.IsSelected = select;
            }
        }
    }

    /// <summary>Called by an item whose check box changed.</summary>
    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(AllSelected));
    }

    [RelayCommand]
    private void StopSelecting()
    {
        IsSelecting = false;
        foreach (SentItemViewModel item in Items)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private Task RemoveSelectedAsync() => RemoveAsync([.. Items.Where(i => i.IsSelected)]);

    /// <summary>
    /// Takes offers out of "Đã gửi"; the files sent stay where they are. Asked first for several offers, or for one still
    /// open (it is withdrawn).
    /// </summary>
    internal async Task RemoveAsync(IReadOnlyList<SentItemViewModel> chosen)
    {
        if (chosen.Count == 0)
        {
            return;
        }

        if (chosen.Count > 1 || chosen.Any(i => i.CanWithdraw))
        {
            string message = chosen.Any(i => i.CanWithdraw)
                ? L.F("Xóa {0} mục khỏi Đã gửi? Mục còn đang mở sẽ bị thu hồi: người nhận chưa tải sẽ không tải được nữa. File gốc của bạn vẫn giữ nguyên.", chosen.Count)
                : L.F("Xóa {0} mục khỏi Đã gửi? File gốc của bạn vẫn giữ nguyên.", chosen.Count);
            if (await DialogService.ChooseAsync(L.T("Xóa"), message, [L.T("Xóa"), L.T("Hủy")]) != 0)
            {
                return;
            }
        }

        foreach (SentItemViewModel item in chosen)
        {
            item.IsLeaving = true;
        }

        await Task.Delay(InboxViewModel.LeaveTime);
        await offers.RemoveAsync([.. chosen.Select(i => i.OfferId)]);
        Refresh();
        if (IsSelecting && !HasSelection)
        {
            IsSelecting = false;
        }
    }

    private void Refresh()
    {
        // Text alone lives in the conversation (docs/17-ui-cleanup.md §2).
        SentOffer[] current = [.. offers.GetOffers().Where(o => o.Paths.Count > 0)];
        HashSet<string> present = [.. current.Select(o => o.OfferId)];
        foreach (SentItemViewModel gone in Items.Where(i => !present.Contains(i.OfferId)).ToList())
        {
            Items.Remove(gone);
            SelectionChanged();
        }

        for (int i = 0; i < current.Length; i++)
        {
            SentItemViewModel? item = Items.FirstOrDefault(x => x.OfferId == current[i].OfferId);
            if (item is null)
            {
                item = new SentItemViewModel(current[i].OfferId, this);
                Items.Insert(Math.Min(i, Items.Count), item);
            }

            item.Update(current[i]);
        }

        UpdateView();
    }
}

/// <summary>One recipient of a sent offer, as a chip.</summary>
public sealed record SentRecipientItem(string Name, string State, bool IsDone, bool IsFailed);

public sealed partial class SentItemViewModel(string offerId, SentViewModel owner) : ObservableObject, IFileRow
{
    public string OfferId { get; } = offerId;

    public DateTimeOffset At { get; private set; }

    IEnumerable<string> IFileRow.SearchFields => [.. Paths, .. Recipients.Select(r => r.Name)];

    /// <summary>Kept by the Files page's filter and search box.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    partial void OnIsShownChanged(bool value) => IsSelected &= value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDay))]
    public partial string? DayText { get; set; }

    public bool HasDay => DayText is not null;

    /// <summary>For the check box shown while "Đã gửi" is in "Chọn".</summary>
    public SentViewModel Owner => owner;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.SelectionChanged();

    /// <summary>Removed: fades and slides away (Themes/Motion.axaml) before it leaves the list.</summary>
    [ObservableProperty]
    public partial bool IsLeaving { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    /// <summary>"6 file · 2,7 MB · 5 phút trước".</summary>
    [ObservableProperty]
    public partial string Details { get; set; } = "";

    [ObservableProperty]
    public partial TransferKind Kind { get; set; }

    /// <summary>Every file and folder of the offer (the title shows only the first).</summary>
    public IReadOnlyList<string> Paths { get; private set; } = [];

    /// <summary>"Xem cả N mục".</summary>
    public OfferContents Contents { get; } = new([]);

    [ObservableProperty]
    public partial IReadOnlyList<SentRecipientItem> Recipients { get; set; } = [];

    [ObservableProperty]
    public partial bool CanWithdraw { get; set; }

    [ObservableProperty]
    public partial bool HasChangedFiles { get; set; }

    public void Update(SentOffer offer)
    {
        DateTimeOffset now = owner.TimeProvider.GetUtcNow();
        At = offer.CreatedAt;
        Paths = offer.Paths;
        Title = TransferText.Summary(offer.Paths);
        Contents.Update(offer.Paths);
        Summary = $"{Title} · {TransferText.Size(offer.TotalBytes)}";
        string[] files = [.. offer.Paths.Where(p => !p.EndsWith('/'))];
        Details = $"{TransferText.FileCount(files.Length)} · {TransferText.Size(offer.TotalBytes)} · {TransferText.Ago(offer.CreatedAt, now)}";
        Kind = TransferText.KindOf(files);

        CanWithdraw = offer.State == OfferState.Open;
        HasChangedFiles = offer.HasChangedFiles && offer.State == OfferState.Open;
        StatusText = offer.State switch
        {
            OfferState.Open => L.F("Đang mở · {0}", TransferText.Remaining(offer.ExpiresAt - now)),
            OfferState.Withdrawn => L.T("Đã thu hồi"),
            OfferState.Expired => L.T("Đã hết hạn"),
            _ => L.T("Hoàn tất"),
        };
        Recipients = [.. offer.Recipients.Select(r => new SentRecipientItem(
            owner.Names.Of(r.DeviceId), Describe(r, offer.TotalBytes), r.State == RecipientState.Completed, r.State == RecipientState.Failed))];
    }

    [RelayCommand]
    private Task WithdrawAsync() => owner.Offers.WithdrawAsync(OfferId);

    /// <summary>"Chọn nhiều": "Đã gửi" shows its check boxes, this offer checked.</summary>
    [RelayCommand]
    private void SelectMany()
    {
        owner.IsSelecting = true;
        IsSelected = true;
    }

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync([this]);

    private string Describe(SentRecipient recipient, long total) => recipient.State switch
    {
        RecipientState.Pending => owner.Names.IsOnline(recipient.DeviceId) ? L.T("đang gửi thông báo…") : L.T("chưa nhận được thông báo (đang offline)"),
        RecipientState.Delivered => L.T("chưa mở"),
        // Range retries can resend bytes, so cap below 100% until the recipient confirms.
        RecipientState.Downloading => L.F("đang tải {0:0}%", Math.Min(99, total == 0 ? 0 : 100.0 * recipient.BytesServed / total)),
        RecipientState.Completed => L.T("đã nhận"),
        RecipientState.Partial => L.T("đã nhận một phần"),
        RecipientState.Declined => L.T("đã bỏ qua"),
        _ => L.T("lỗi (không tải được hoặc không nhận lời gửi)"),
    };
}
