using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.ViewModels;

/// <summary>
/// The phone's one screen (docs/14-android.md, A2): "Xóm" (contacts and who is online, Kết nối) and "Hộp nhận"
/// (offers, download into Download/Shorekeeper). Connect requests show as an overlay.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly PeerDirectory directory;
    private readonly TrustStore trust;
    private readonly PairingService pairing;
    private readonly InboxService inbox;
    private readonly LocalDevice device;
    private readonly AppPaths paths;
    private readonly DispatcherTimer progressTimer;

    public MainViewModel(IServiceProvider services)
    {
        directory = services.GetRequiredService<PeerDirectory>();
        trust = services.GetRequiredService<TrustStore>();
        pairing = services.GetRequiredService<PairingService>();
        inbox = services.GetRequiredService<InboxService>();
        device = services.GetRequiredService<LocalDevice>();
        paths = services.GetRequiredService<AppPaths>();

        directory.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPeers);
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPeers);
        inbox.OfferArrived += (_, _) => Dispatcher.UIThread.Post(RefreshOffers);
        inbox.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshOffers);
        pairing.IncomingRequested += (_, request) => Dispatcher.UIThread.Post(() => Incoming = new IncomingPairingViewModel(request, this));
        pairing.IncomingClosed += (_, id) => Dispatcher.UIThread.Post(() =>
        {
            if (Incoming?.Id == id)
            {
                Incoming = null;
            }
        });

        // Progress is read from the engine once a second while something downloads.
        progressTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshOffers());
        RefreshPeers();
        RefreshOffers();
    }

    public string Title => device.DisplayName;

    public string Subtitle => $"{LocalDevice.HostName} · {device.DeviceId.ShortForm} · nhận vào {paths.DefaultDownloadDirectory}";

    public ObservableCollection<PeerItemViewModel> Peers { get; } = [];

    public ObservableCollection<OfferItemViewModel> Offers { get; } = [];

    public bool HasNoPeers => Peers.Count == 0;

    public bool HasNoOffers => Offers.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIncoming))]
    public partial IncomingPairingViewModel? Incoming { get; set; }

    public bool HasIncoming => Incoming is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutgoing))]
    public partial OutgoingPairingViewModel? Outgoing { get; set; }

    public bool HasOutgoing => Outgoing is not null;

    internal string NameOf(DeviceId id) =>
        trust.Get(id)?.ShownName ?? directory.Snapshot().FirstOrDefault(p => p.DeviceId == id)?.DisplayName ?? id.ShortForm;

    internal async Task ConnectAsync(PeerItemViewModel peer)
    {
        var outgoing = new OutgoingPairingViewModel(peer.Name, this);
        Outgoing = outgoing;
        PairingOutcome outcome;
        try
        {
            outcome = await pairing.RequestAsync(peer.DeviceId, null, pending => Dispatcher.UIThread.Post(() => outgoing.Code = Format(pending.Code)), outgoing.Cancellation.Token);
        }
        catch (Exception ex) when (ex is Engine.Api.PeerUnreachableException or Engine.Api.PeerApiException or HttpRequestException)
        {
            outgoing.Finish($"Không liên lạc được với {peer.Name}.");
            return;
        }
        catch (OperationCanceledException)
        {
            Outgoing = null;
            return;
        }

        outgoing.Finish(outcome switch
        {
            PairingOutcome.Accepted => $"Đã kết nối với {peer.Name}.",
            PairingOutcome.Declined => $"{peer.Name} đã từ chối.",
            PairingOutcome.Expired => "Yêu cầu đã hết hạn.",
            _ => "Đã hủy.",
        });
    }

    internal void CloseOutgoing() => Outgoing = null;

    internal void Accept(IncomingPairingViewModel request)
    {
        pairing.Accept(request.Id);
        Incoming = null;
    }

    internal void Decline(IncomingPairingViewModel request)
    {
        pairing.Decline(request.Id);
        Incoming = null;
    }

    internal void Download(OfferItemViewModel offer) => inbox.Download(offer.Key);

    internal Task DeclineAsync(OfferItemViewModel offer) => inbox.DeclineAsync(offer.Key);

    /// <summary>"482913" → "482 913", as the desktop shows it.</summary>
    internal static string Format(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    private void RefreshPeers()
    {
        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);
        List<PeerItemViewModel> items =
        [
            .. trust.GetAll(TrustLevel.Trusted).Select(record => new PeerItemViewModel(
                record.DeviceId, record.ShownName, Detail(live.GetValueOrDefault(record.DeviceId), record.HostName), true, live.ContainsKey(record.DeviceId), this)),
            .. live.Values
                .Where(peer => trust.GetLevel(peer.DeviceId) == TrustLevel.Unknown)
                .Select(peer => new PeerItemViewModel(peer.DeviceId, peer.DisplayName, Detail(peer, peer.HostName), false, true, this)),
        ];
        items.Sort((x, y) => x.IsTrusted != y.IsTrusted ? (x.IsTrusted ? -1 : 1) : CultureInfo.CurrentCulture.CompareInfo.Compare(x.Name, y.Name, CompareOptions.IgnoreCase));

        Peers.Clear();
        foreach (PeerItemViewModel item in items)
        {
            Peers.Add(item);
        }

        OnPropertyChanged(nameof(HasNoPeers));
    }

    private void RefreshOffers()
    {
        IReadOnlyList<ReceivedOffer> current = inbox.GetOffers();
        Offers.Clear();
        foreach (ReceivedOffer offer in current.OrderByDescending(o => o.ReceivedAt))
        {
            Offers.Add(new OfferItemViewModel(offer, NameOf(offer.SenderId), this));
        }

        progressTimer.IsEnabled = current.Any(o => o.State == InboxState.Downloading);
        OnPropertyChanged(nameof(HasNoOffers));
    }

    private static string Detail(PeerInfo? live, string host) =>
        live is null ? $"{host} · offline" : $"{live.HostName} · {live.Address}{(live.IsBusy ? " · bận" : "")}";
}

public sealed partial class PeerItemViewModel(DeviceId deviceId, string name, string detail, bool isTrusted, bool isOnline, MainViewModel owner)
{
    public DeviceId DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public bool IsTrusted { get; } = isTrusted;

    public bool CanConnect { get; } = !isTrusted && isOnline;

    public string Badge { get; } = isTrusted ? "✔ Đã kết nối" : "⚠ Chưa kết nối";

    [RelayCommand]
    private Task ConnectAsync() => owner.ConnectAsync(this);
}

public sealed partial class OfferItemViewModel(ReceivedOffer offer, string senderName, MainViewModel owner)
{
    public OfferKey Key { get; } = new(offer.SenderId, offer.OfferId);

    public string Title { get; } = $"{senderName} gửi cho bạn";

    public string Summary { get; } = $"{TransferText.Summary(offer.Files.Select(f => f.RelativePath))} · {TransferText.Size(offer.TotalBytes)}";

    public string? Note { get; } = offer.Note;

    public bool HasNote => Note is not null;

    public double Progress { get; } = offer.TotalBytes == 0 ? 0 : 100.0 * offer.BytesReceived / offer.TotalBytes;

    public bool IsDownloading { get; } = offer.State == InboxState.Downloading;

    public string Status { get; } = offer.State switch
    {
        InboxState.New => "Mới",
        InboxState.Downloading when offer.ReconnectingUntil is not null => "Mất kết nối, đang thử lại…",
        InboxState.Downloading => $"Đang tải {TransferText.Size(offer.BytesReceived)} / {TransferText.Size(offer.TotalBytes)}",
        InboxState.Completed => "Đã tải xong",
        InboxState.Partial => "Đã tải một phần",
        InboxState.Failed => $"Lỗi: {offer.Error}",
        InboxState.Cancelled => "Đã dừng",
        InboxState.Declined => "Đã bỏ qua",
        _ => offer.State.ToString(),
    };

    public bool CanDownload { get; } = offer.State is InboxState.New or InboxState.Failed or InboxState.Partial or InboxState.Cancelled;

    public bool CanDecline { get; } = offer.State == InboxState.New;

    [RelayCommand]
    private void Download() => owner.Download(this);

    [RelayCommand]
    private Task DeclineAsync() => owner.DeclineAsync(this);
}

public sealed partial class IncomingPairingViewModel(IncomingPairing request, MainViewModel owner)
{
    public string Id { get; } = request.Id;

    public string Title { get; } = $"{request.Name} muốn kết nối";

    public string Host { get; } = request.Host;

    public string Code { get; } = MainViewModel.Format(request.Code);

    [RelayCommand]
    private void Accept() => owner.Accept(this);

    [RelayCommand]
    private void Decline() => owner.Decline(this);
}

public sealed partial class OutgoingPairingViewModel(string name, MainViewModel owner) : ObservableObject
{
    public CancellationTokenSource Cancellation { get; } = new();

    public string Title { get; } = $"Kết nối với {name}";

    [ObservableProperty]
    public partial string Code { get; set; } = "…";

    [ObservableProperty]
    public partial string Status { get; set; } = "Đang gửi yêu cầu… So mã này với mã trên máy kia.";

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    public void Finish(string status)
    {
        Status = status;
        IsFinished = true;
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsFinished)
        {
            Cancellation.Cancel();
        }

        owner.CloseOutgoing();
    }
}
