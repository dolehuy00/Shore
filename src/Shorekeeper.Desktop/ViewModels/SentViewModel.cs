using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

public sealed partial class SentItemViewModel(string offerId, SentViewModel owner) : ObservableObject
{
    public string OfferId { get; } = offerId;

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string RecipientsText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanWithdraw { get; set; }

    [ObservableProperty]
    public partial bool HasChangedFiles { get; set; }

    public void Update(SentOffer offer)
    {
        DateTimeOffset now = owner.TimeProvider.GetUtcNow();
        Summary = $"{TransferText.Summary(offer.Paths)} · {TransferText.Size(offer.TotalBytes)}";
        CanWithdraw = offer.State == OfferState.Open;
        HasChangedFiles = offer.HasChangedFiles && offer.State == OfferState.Open;
        StatusText = offer.State switch
        {
            OfferState.Open => $"Gửi lúc {offer.CreatedAt.ToLocalTime():HH:mm dd/MM} · mở {TransferText.Remaining(offer.ExpiresAt - now)}",
            OfferState.Withdrawn => "Đã thu hồi",
            OfferState.Expired => "Đã hết hạn",
            _ => "Hoàn tất",
        };
        RecipientsText = string.Join("\n", offer.Recipients.Select(r => $"{owner.Names.Of(r.DeviceId)}: {Describe(r, offer.TotalBytes)}"));
    }

    [RelayCommand]
    private Task WithdrawAsync() => owner.Offers.WithdrawAsync(OfferId);

    private string Describe(SentRecipient recipient, long total) => recipient.State switch
    {
        RecipientState.Pending => owner.Names.IsOnline(recipient.DeviceId) ? "đang gửi thông báo…" : "chưa nhận được thông báo (đang offline)",
        RecipientState.Delivered => "chưa mở",
        // Range retries can resend bytes, so cap below 100% until the recipient confirms.
        RecipientState.Downloading => $"đang tải {Math.Min(99, total == 0 ? 0 : 100.0 * recipient.BytesServed / total):0}%",
        RecipientState.Completed => "đã nhận",
        RecipientState.Partial => "đã nhận một phần",
        RecipientState.Declined => "đã bỏ qua",
        _ => "lỗi (không tải được hoặc không nhận lời gửi)",
    };
}
