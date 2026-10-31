using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Android.Platform;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Android.ViewModels;

/// <summary>
/// "Thêm máy bằng địa chỉ" (the + on Xóm), for Wi-Fi that hides devices from each other (R7): asks the address who it is,
/// keeps the address so the phone finds it again, then connects.
/// </summary>
public sealed partial class AddPeerViewModel(ManualPeerFinder finder, MainViewModel owner) : ObservableObject
{
    [ObservableProperty]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; set; }

    public bool HasStatus => Status is not null;

    [RelayCommand]
    private async Task FindAsync(CancellationToken cancellationToken)
    {
        Status = L.T("Đang tìm…");
        FindResult result = await finder.FindAsync(Address, cancellationToken);
        if (result.Peer is not { } peer)
        {
            Status = result.Error;
            return;
        }

        await finder.SaveAsync(Address, cancellationToken);
        owner.AddPeer = null;
        await owner.ConnectAsync(peer.DeviceId, peer.Name);
    }

    /// <summary>"Quét mã QR": the code "Mã QR" shows on a computer; reached at its addresses, then connected with its invitation.</summary>
    [RelayCommand]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        string? text;
        try
        {
            text = await QrScanner.ScanAsync(global::Android.App.Application.Context);
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return;
        }

        if (text is null)
        {
            return;
        }

        if (!PairingInviteLink.TryParse(text, out PairingInviteLink? link))
        {
            Status = L.T("Đây không phải mã QR kết nối của Shorekeeper.");
            return;
        }

        Status = L.T("Đang tìm…");
        FindResult result = await finder.FindAsync(link, cancellationToken);
        if (result.Peer is not { } peer)
        {
            Status = result.Error;
            return;
        }

        owner.AddPeer = null;
        await owner.ConnectAsync(peer.DeviceId, peer.Name, link.Invite);
    }

    [RelayCommand]
    private void Close()
    {
        FindCommand.Cancel();
        ScanCommand.Cancel();
        owner.AddPeer = null;
    }
}
