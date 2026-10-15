using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Android.Platform;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.ViewModels;

/// <summary>
/// The phone's one screen (docs/14-android.md, A2–A5): "Xóm" (contacts and who is online, Kết nối, Gửi file),
/// "Hộp nhận" (download into Download/Shorekeeper), "Đã gửi", "Nhóm" and "Cài đặt". Connect requests and the
/// recipients of a share show as overlays.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly PeerDirectory directory;
    private readonly TrustStore trust;
    private readonly PairingService pairing;
    private readonly InboxService inbox;
    private readonly OfferService offers;
    private readonly IExternalFileSource external;
    private readonly GroupStore groups;
    private readonly SettingsService settings;
    private readonly LocalDevice device;
    private readonly AppPaths paths;
    private readonly DispatcherTimer progressTimer;

    public MainViewModel(IServiceProvider services)
    {
        directory = services.GetRequiredService<PeerDirectory>();
        trust = services.GetRequiredService<TrustStore>();
        pairing = services.GetRequiredService<PairingService>();
        inbox = services.GetRequiredService<InboxService>();
        offers = services.GetRequiredService<OfferService>();
        external = services.GetRequiredService<IExternalFileSource>();
        groups = services.GetRequiredService<GroupStore>();
        settings = services.GetRequiredService<SettingsService>();
        device = services.GetRequiredService<LocalDevice>();
        paths = services.GetRequiredService<AppPaths>();
        Groups = new GroupsTabViewModel(services, this);
        Settings = new SettingsTabViewModel(services, this);
        settings.Changed += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Title)));

        directory.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPeers);
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPeers);
        inbox.OfferArrived += (_, _) => Dispatcher.UIThread.Post(RefreshOffers);
        inbox.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshOffers);
        offers.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshSent);
        pairing.IncomingRequested += (_, request) => Dispatcher.UIThread.Post(() => Incoming = new IncomingPairingViewModel(request, this));
        pairing.IncomingClosed += (_, id) => Dispatcher.UIThread.Post(() =>
        {
            if (Incoming?.Id == id)
            {
                Incoming = null;
            }
        });

        // Progress is read from the engine once a second while something downloads.
        progressTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            RefreshOffers();
            RefreshSent();
        });
        RefreshPeers();
        RefreshOffers();
        RefreshSent();
    }

    public string Title => device.DisplayName;

    public string Subtitle => $"{LocalDevice.HostName} · {device.DeviceId.ShortForm} · nhận vào {paths.DefaultDownloadDirectory}";

    public GroupsTabViewModel Groups { get; }

    public SettingsTabViewModel Settings { get; }

    public ObservableCollection<PeerItemViewModel> Peers { get; } = [];

    public ObservableCollection<OfferItemViewModel> Offers { get; } = [];

    public ObservableCollection<SentItemViewModel> Sent { get; } = [];

    public bool HasNoSent => Sent.Count == 0;

    /// <summary>0 Xóm, 1 Hộp nhận, 2 Đã gửi, 3 Nhóm, 4 Cài đặt.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShare))]
    public partial ShareViewModel? Share { get; set; }

    public bool HasShare => Share is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    public bool HasNotice => Notice is not null;

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

    internal async Task ConnectAsync(DeviceId peer, string name)
    {
        var outgoing = new OutgoingPairingViewModel(name, this);
        Outgoing = outgoing;
        PairingOutcome outcome;
        try
        {
            outcome = await pairing.RequestAsync(peer, null, pending => Dispatcher.UIThread.Post(() => outgoing.Code = Format(pending.Code)), outgoing.Cancellation.Token);
        }
        catch (Exception ex) when (ex is Engine.Api.PeerUnreachableException or Engine.Api.PeerApiException or HttpRequestException)
        {
            outgoing.Finish($"Không liên lạc được với {name}.");
            return;
        }
        catch (OperationCanceledException)
        {
            Outgoing = null;
            return;
        }

        outgoing.Finish(outcome switch
        {
            PairingOutcome.Accepted => $"Đã kết nối với {name}.",
            PairingOutcome.Declined => $"{name} đã từ chối.",
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

    /// <summary>Items from the share sheet: ask whom to send them to — contacts, and fellow group members through their group.</summary>
    public void ShowShare(IReadOnlyList<string> locations)
    {
        IReadOnlyList<PeerInfo> live = directory.Snapshot();
        List<PickablePerson> people =
        [
            .. trust.GetAll(TrustLevel.Trusted)
                .Select(record => new PickablePerson(record.DeviceId, record.ShownName, Detail(live.FirstOrDefault(p => p.DeviceId == record.DeviceId), record.HostName), null)),
        ];
        if (settings.Current.GroupsEnabled)
        {
            foreach (Group group in groups.GetAll().Where(g => g.Role != GroupRole.Pending))
            {
                foreach (GroupMember member in group.Members.Where(m => m.DeviceId != device.DeviceId
                    && trust.GetLevel(m.DeviceId) == TrustLevel.Unknown && people.TrueForAll(p => p.DeviceId != m.DeviceId)))
                {
                    PeerInfo? peer = live.FirstOrDefault(p => p.DeviceId == member.DeviceId);
                    people.Add(new PickablePerson(member.DeviceId, peer?.DisplayName ?? member.Name, $"{Detail(peer, member.Name)} · nhóm {group.Name}", group.Id));
                }
            }
        }

        Share = new ShareViewModel(locations, [.. people.OrderBy(p => p.Detail.Contains("offline", StringComparison.Ordinal)).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)], this);
    }

    internal void CloseShare() => Share = null;

    /// <summary>
    /// Sends files picked in the app or shared from another app; locations may be paths or content:// URIs.
    /// One offer for contacts, one per group for fellow members who are not contacts (docs/07-groups.md §4).
    /// </summary>
    internal async Task<string?> SendAsync(IReadOnlyList<string> locations, IReadOnlyList<(DeviceId Id, string? GroupId)> recipients)
    {
        PreparedOffer prepared;
        try
        {
            prepared = await Task.Run(() => OfferService.Prepare(locations, external));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        if (prepared.Files.Count == 0)
        {
            return "Không đọc được file nào trong số đã chọn.";
        }

        foreach (var batch in recipients.GroupBy(r => r.GroupId))
        {
            await offers.CreateAsync(prepared, [.. batch.Select(r => r.Id)], null, snapshotLocked: false, groupId: batch.Key, CancellationToken.None);
        }

        SelectedTab = 2;
        return prepared.Unreadable.Count > 0 ? $"{prepared.Unreadable.Count} mục không đọc được đã bị bỏ qua." : null;
    }

    internal Task WithdrawAsync(SentItemViewModel offer) => offers.WithdrawAsync(offer.OfferId);

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

        progressTimer.IsEnabled = current.Any(o => o.State == InboxState.Downloading)
            || offers.GetOffers().Any(o => o.Recipients.Any(r => r.State == RecipientState.Downloading));
        OnPropertyChanged(nameof(HasNoOffers));
    }

    private void RefreshSent()
    {
        IReadOnlyList<SentOffer> current = offers.GetOffers();
        Sent.Clear();
        foreach (SentOffer offer in current.OrderByDescending(o => o.CreatedAt))
        {
            Sent.Add(new SentItemViewModel(offer, this));
        }

        progressTimer.IsEnabled |= current.Any(o => o.Recipients.Any(r => r.State == RecipientState.Downloading));
        OnPropertyChanged(nameof(HasNoSent));
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

    /// <summary>Contacts receive files; the picker is opened by the view (it needs the window).</summary>
    public bool CanSend { get; } = isTrusted;

    internal Task<string?> SendAsync(IReadOnlyList<string> locations) => owner.SendAsync(locations, [(DeviceId, null)]);

    [RelayCommand]
    private Task ConnectAsync() => owner.ConnectAsync(DeviceId, Name);
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

public sealed partial class SentItemViewModel(SentOffer offer, MainViewModel owner)
{
    public string OfferId { get; } = offer.OfferId;

    public string Summary { get; } = $"{TransferText.Summary(offer.Paths)} · {TransferText.Size(offer.TotalBytes)}";

    public string Recipients { get; } = string.Join("\n", offer.Recipients.Select(r => $"{owner.NameOf(r.DeviceId)}: {State(r.State)}"));

    public string Status { get; } = offer.State switch
    {
        OfferState.Open => offer.HasChangedFiles ? "Đang mở · có file đã bị sửa" : "Đang mở",
        OfferState.Withdrawn => "Đã thu hồi",
        OfferState.Expired => "Đã hết hạn",
        _ => "Xong",
    };

    public bool CanWithdraw { get; } = offer.State == OfferState.Open;

    [RelayCommand]
    private Task WithdrawAsync() => owner.WithdrawAsync(this);

    private static string State(RecipientState state) => state switch
    {
        RecipientState.Pending => "chờ gửi (đang offline)",
        RecipientState.Delivered => "chưa mở",
        RecipientState.Downloading => "đang tải",
        RecipientState.Completed => "đã nhận",
        RecipientState.Partial => "đã nhận một phần",
        RecipientState.Failed => "lỗi",
        _ => "đã bỏ qua",
    };
}

/// <param name="groupId">Null for contacts; for fellow members, the group the offer goes through.</param>
public sealed partial class PickablePerson(DeviceId deviceId, string name, string detail, string? groupId) : ObservableObject
{
    public DeviceId DeviceId { get; } = deviceId;

    public string? GroupId { get; } = groupId;

    public string Name { get; } = name;

    public string Detail { get; } = detail;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>"Gửi cho ai?" after sharing from another app (Gallery, Files…).</summary>
public sealed partial class ShareViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> locations;
    private readonly MainViewModel owner;

    public ShareViewModel(IReadOnlyList<string> locations, IReadOnlyList<PickablePerson> people, MainViewModel owner)
    {
        this.locations = locations;
        this.owner = owner;
        People = people;
        foreach (PickablePerson person in people)
        {
            person.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanSend));
        }
    }

    public string Title => $"Gửi {locations.Count} mục cho…";

    public IReadOnlyList<PickablePerson> People { get; }

    public bool HasNobody => People.Count == 0;

    public bool CanSend => !IsSending && People.Any(p => p.IsChecked);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial bool IsSending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private async Task SendAsync()
    {
        IsSending = true;
        Error = await owner.SendAsync(locations, [.. People.Where(p => p.IsChecked).Select(p => (p.DeviceId, p.GroupId))]);
        IsSending = false;
        if (Error is null)
        {
            owner.CloseShare();
        }
    }

    [RelayCommand]
    private void Cancel() => owner.CloseShare();
}
