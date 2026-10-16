using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>"Đã gửi" (docs/10-ux.md §3): our offers and where each recipient is at.</summary>
public sealed class SentViewModel
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

    internal PeerNames Names { get; }

    internal TimeProvider TimeProvider { get; }

    internal OfferService Offers => offers;

    private void Refresh()
    {
        IReadOnlyList<SentOffer> current = offers.GetOffers();
        for (int i = 0; i < current.Count; i++)
        {
            SentItemViewModel? item = Items.FirstOrDefault(x => x.OfferId == current[i].OfferId);
            if (item is null)
            {
                item = new SentItemViewModel(current[i].OfferId, this);
                Items.Insert(Math.Min(i, Items.Count), item);
            }

            item.Update(current[i]);
        }
    }
}

/// <summary>One recipient of a sent offer, as a chip.</summary>
public sealed record SentRecipientItem(string Name, string State, bool IsDone, bool IsFailed);

public sealed partial class SentItemViewModel(string offerId, SentViewModel owner) : ObservableObject
{
    public string OfferId { get; } = offerId;

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

    [ObservableProperty]
    public partial IReadOnlyList<SentRecipientItem> Recipients { get; set; } = [];

    [ObservableProperty]
    public partial bool CanWithdraw { get; set; }

    [ObservableProperty]
    public partial bool HasChangedFiles { get; set; }

    public void Update(SentOffer offer)
    {
        DateTimeOffset now = owner.TimeProvider.GetUtcNow();
        Title = TransferText.Summary(offer.Paths);
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
