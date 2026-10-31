using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.UI.ViewModels;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Android.ViewModels;

/// <summary>An offer in "Hộp nhận"; kept and updated as it changes, so the list is not rebuilt while scrolling.</summary>
public sealed partial class OfferItemViewModel(ReceivedOffer offer, MainViewModel owner) : ObservableObject, ISelectableItem
{
    private ReceivedOffer current = offer;

    /// <summary>Received files still on the disk, read by <see cref="CheckFiles"/>; null until read.</summary>
    private (int Present, int Received)? onDisk;
    private InboxState? checkedState;

    public OfferKey Key { get; } = new(offer.SenderId, offer.OfferId);

    /// <summary>For the check box shown while Hộp nhận is in "Chọn".</summary>
    public ListSelection Selection => owner.OfferSelection;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.OfferSelection.Changed();

    /// <summary>Removed: fades and slides away (Themes/Motion.axaml) before it leaves the list.</summary>
    [ObservableProperty]
    public partial bool IsLeaving { get; set; }

    /// <summary>"Xóa kèm file đã nhận": some received file is still there.</summary>
    [ObservableProperty]
    public partial bool HasFiles { get; set; }

    public string? Note { get; } = offer.Note;

    public bool HasNote => Note is not null;

    public OfferContents Contents { get; } = new([]);


    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial TransferKind Kind { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool CanDownload { get; set; }

    [ObservableProperty]
    public partial bool CanDecline { get; set; }

    public void Update(ReceivedOffer value, string senderName)
    {
        current = value;
        // Files that just arrived are there; others are read from the disk when the tab is shown.
        if (checkedState != value.State)
        {
            checkedState = value.State;
            onDisk = null;
        }

        bool allGone = onDisk is { Received: > 0, Present: 0 };
        Title = L.F("{0} gửi cho bạn", senderName);
        Summary = $"{TransferText.Summary(value.Files.Select(f => f.RelativePath))} · {TransferText.Size(value.TotalBytes)}";
        Contents.Update([.. value.Files.Select(f => f.RelativePath)]);
        Kind = TransferText.KindOf([.. value.Files.Where(f => !f.IsDirectory).Select(f => f.RelativePath)]);
        IsCompleted = value.State == InboxState.Completed;
        Progress = value.TotalBytes == 0 ? 0 : 100.0 * value.BytesReceived / value.TotalBytes;
        IsDownloading = value.State == InboxState.Downloading;
        Status = value.State switch
        {
            InboxState.New => L.T("Mới"),
            InboxState.Downloading when value.ReconnectingUntil is not null => L.T("Mất kết nối, đang thử lại…"),
            InboxState.Downloading => L.F("Đang tải {0} / {1}", TransferText.Size(value.BytesReceived), TransferText.Size(value.TotalBytes)),
            InboxState.Completed => L.T("Đã tải xong"),
            InboxState.Partial => L.T("Đã tải một phần"),
            InboxState.Failed => L.F("Lỗi: {0}", value.Error),
            InboxState.Cancelled => L.T("Đã dừng"),
            InboxState.Declined => L.T("Đã bỏ qua"),
            InboxState.Withdrawn => L.T("Người gửi đã thu hồi"),
            _ => L.T("Đã hết hạn"),
        };
        if (onDisk is { } files && TransferText.Gone(files) is { } gone && value.State is InboxState.Completed or InboxState.Partial)
        {
            Status = allGone ? gone : $"{Status} · {gone}";
        }

        IsCompleted &= !allGone;
        HasFiles = value.Files.Any(f => !f.IsDirectory && f.State == InboxFileState.Completed) && !allGone;
        CanDownload = value.State is InboxState.New or InboxState.Failed or InboxState.Partial or InboxState.Cancelled;
        CanDecline = value.State == InboxState.New;
    }

    [RelayCommand]
    private void Download() => owner.Download(this);

    [RelayCommand]
    private Task DeclineAsync() => owner.DeclineAsync(this);

    /// <summary>Reads the disk for received files deleted or moved since.</summary>
    public void CheckFiles()
    {
        onDisk = TransferText.OnDisk(current.Files);
        Update(current, owner.NameOf(current.SenderId));
    }

    [RelayCommand]
    private void SelectMany() => Selection.StartWith(this);

    [RelayCommand]
    private void Remove() => owner.RemoveOffers([this], deleteFiles: false);

    [RelayCommand]
    private void RemoveWithFiles() => owner.RemoveOffers([this], deleteFiles: true);
}
