using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>
/// "Kết nối bằng mã QR" (docs/10-ux.md §5): a QR code with this computer's addresses and a one-time invitation. A phone
/// that scans it connects without the code being compared; then a new code is shown for the next one. The invitation is
/// renewed before it expires and ends when the window closes.
/// </summary>
public sealed partial class PairingQrViewModel : DialogViewModel, IDisposable
{
    private readonly PairingService pairing;
    private readonly DispatcherTimer renew;
    private PairingInviteLink link;

    public PairingQrViewModel(PairingService pairing)
    {
        this.pairing = pairing;
        link = Show(pairing.CreateInvite());
        renew = new DispatcherTimer(PairingService.InviteLifetime - TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Renew());
        renew.Start();
        pairing.JoinedByInvite += OnJoined;
    }

    [ObservableProperty]
    public partial Bitmap? Code { get; set; }

    /// <summary>"192.168.10.28 · 192.168.137.1": where the phone will look.</summary>
    [ObservableProperty]
    public partial string Addresses { get; set; } = "";

    /// <summary>No network: there is no address to put in the code.</summary>
    [ObservableProperty]
    public partial bool HasNoAddress { get; set; }

    /// <summary>"Đã kết nối với OPPO Reno10 5G."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJoined))]
    public partial string? Joined { get; set; }

    public bool HasJoined => Joined is not null;

    public void Dispose()
    {
        pairing.JoinedByInvite -= OnJoined;
        renew.Stop();
        pairing.EndInvite(link.Invite);
        Code?.Dispose();
    }

    [RelayCommand]
    private void Close() => RequestClose();

    private void OnJoined(object? sender, IncomingPairing request) => Dispatcher.UIThread.Post(() =>
    {
        Joined = L.F("Đã kết nối với {0}.", request.Name);
        Renew();
    });

    private void Renew()
    {
        pairing.EndInvite(link.Invite);
        link = Show(pairing.CreateInvite());
        renew.Stop();
        renew.Start();
    }

    private PairingInviteLink Show(PairingInviteLink next)
    {
        HasNoAddress = next.Endpoints.Count == 0;
        Addresses = string.Join(" · ", next.Endpoints.Select(e => e.Address.ToString()));
        using QRCodeData data = QRCodeGenerator.GenerateQrCode(next.Format(), QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        Bitmap? old = Code;
        using (var stream = new MemoryStream(png))
        {
            Code = new Bitmap(stream);
        }

        old?.Dispose();
        return next;
    }
}
