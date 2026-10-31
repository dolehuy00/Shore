using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.UI.ViewModels;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Android.ViewModels;

/// <summary>An offer in "Đã gửi"; kept and updated as it changes, so the list is not rebuilt while scrolling.</summary>
public sealed partial class SentItemViewModel(SentOffer offer, MainViewModel owner) : ObservableObject, ISelectableItem
{
    public string OfferId { get; } = offer.OfferId;

    /// <summary>For the check box shown while Đã gửi is in "Chọn".</summary>
    public ListSelection Selection => owner.SentSelection;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.SentSelection.Changed();

    /// <summary>Removed: fades and slides away (Themes/Motion.axaml) before it leaves the list.</summary>
    [ObservableProperty]
    public partial bool IsLeaving { get; set; }

    public OfferContents Contents { get; } = new([]);

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string Recipients { get; set; } = "";

    [ObservableProperty]
    public partial TransferKind Kind { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool CanWithdraw { get; set; }

    public void Update(SentOffer value)
    {
        Summary = $"{TransferText.Summary(value.Paths)} · {TransferText.Size(value.TotalBytes)}";
        Contents.Update(value.Paths);
        Recipients = string.Join("\n", value.Recipients.Select(r => $"{owner.NameOf(r.DeviceId)}: {State(r.State)}"));
        Kind = TransferText.KindOf([.. value.Paths.Where(p => !p.EndsWith('/'))]);
        Status = value.State switch
        {
            OfferState.Open => value.HasChangedFiles ? L.T("Đang mở · có file đã bị sửa") : L.T("Đang mở"),
            OfferState.Withdrawn => L.T("Đã thu hồi"),
            OfferState.Expired => L.T("Đã hết hạn"),
            _ => L.T("Hoàn tất"),
        };
        CanWithdraw = value.State == OfferState.Open;
    }

    [RelayCommand]
    private Task WithdrawAsync() => owner.WithdrawAsync(this);

    [RelayCommand]
    private void SelectMany() => Selection.StartWith(this);

    [RelayCommand]
    private void Remove() => owner.RemoveSent([this]);

    private static string State(RecipientState state) => state switch
    {
        RecipientState.Pending => L.T("chờ gửi (đang offline)"),
        RecipientState.Delivered => L.T("chưa mở"),
        RecipientState.Downloading => L.T("đang tải"),
        RecipientState.Completed => L.T("đã nhận"),
        RecipientState.Partial => L.T("đã nhận một phần"),
        RecipientState.Failed => L.T("lỗi"),
        _ => L.T("đã bỏ qua"),
    };
}
