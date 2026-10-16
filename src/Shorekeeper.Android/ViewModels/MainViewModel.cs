using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Android.Platform;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;
using GeneratedRegexAttribute = System.Text.RegularExpressions.GeneratedRegexAttribute;
using Regex = System.Text.RegularExpressions.Regex;

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

    /// <summary>Offers whose "Xem cả N mục" list is open, as the inbox and sent lists are rebuilt on every change.</summary>
    private readonly HashSet<string> expandedOffers = [];
    private readonly ManualPeerFinder finder;
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
        finder = services.GetRequiredService<ManualPeerFinder>();
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

    public string Subtitle => L.F("{0} · nhận vào {1}", device.DeviceId.ShortForm, SharedStoragePrefix().Replace(paths.DefaultDownloadDirectory, ""));

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuestion))]
    public partial QuestionViewModel? Question { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddPeer))]
    public partial AddPeerViewModel? AddPeer { get; set; }

    public bool HasAddPeer => AddPeer is not null;

    /// <summary>The + on Xóm.</summary>
    [RelayCommand]
    private void OpenAddPeer() => AddPeer = new AddPeerViewModel(finder, this);

    public bool HasQuestion => Question is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoAccept))]
    public partial AutoAcceptChoiceViewModel? AutoAccept { get; set; }

    public bool HasAutoAccept => AutoAccept is not null;

    /// <summary>"Tự nhận file…" on a contact: off, the limit in Settings, or its own.</summary>
    internal void ChooseAutoAccept(PeerItemViewModel peer) =>
        AutoAccept = new AutoAcceptChoiceViewModel(
            peer.Name, trust.Get(peer.DeviceId)?.AutoAcceptMaxBytes, settings.Current.AutoAcceptLimitGb,
            maxBytes => trust.SetAutoAcceptAsync(peer.DeviceId, maxBytes), () => AutoAccept = null);

    internal void AskAlias(PeerItemViewModel peer) =>
        Question = new QuestionViewModel(L.T("Đặt tên gợi nhớ"), L.F("Tên gợi nhớ cho {0}:", peer.Host), L.T("Lưu"), peer.Name,
            alias => trust.SetAliasAsync(peer.DeviceId, alias), this);

    internal void ConfirmDisconnect(PeerItemViewModel peer) =>
        Question = new QuestionViewModel(L.F("Ngắt kết nối với {0}?", peer.Name), L.T("Muốn gửi file cho nhau lại thì phải Kết nối lại."), L.T("Ngắt kết nối"), null,
            _ => pairing.DisconnectAsync(peer.DeviceId), this);

    internal void ConfirmBlock(PeerItemViewModel peer) =>
        Question = new QuestionViewModel(L.F("Chặn {0}?", peer.Name),
            L.T("Máy này sẽ không gửi được yêu cầu kết nối hay file cho bạn nữa. Bỏ chặn trong Cài đặt."), L.T("Chặn"), null,
            _ => trust.BlockAsync(peer.DeviceId, peer.Name, peer.Host), this);

    internal OfferContents ContentsOf(string offerId, IReadOnlyCollection<string> relativePaths) =>
        new(relativePaths, expandedOffers.Contains(offerId), expanded =>
        {
            if (expanded)
            {
                expandedOffers.Add(offerId);
            }
            else
            {
                expandedOffers.Remove(offerId);
            }
        });

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
            outgoing.Finish(L.F("Không liên lạc được với {0}.", name));
            return;
        }
        catch (OperationCanceledException)
        {
            Outgoing = null;
            return;
        }

        outgoing.Finish(outcome switch
        {
            PairingOutcome.Accepted => L.F("Đã kết nối với {0}.", name),
            PairingOutcome.Declined => L.F("{0} đã từ chối.", name),
            PairingOutcome.Expired => L.T("Yêu cầu đã hết hạn."),
            _ => L.T("Đã hủy."),
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
                    people.Add(new PickablePerson(member.DeviceId, peer?.DisplayName ?? member.Name, L.F("{0} · nhóm {1}", Detail(peer, member.Name), group.Name), group.Id));
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
            return L.T("Không đọc được file nào trong số đã chọn.");
        }

        foreach (var batch in recipients.GroupBy(r => r.GroupId))
        {
            await offers.CreateAsync(prepared, [.. batch.Select(r => r.Id)], null, snapshotLocked: false, groupId: batch.Key, CancellationToken.None);
        }

        SelectedTab = 2;
        return prepared.Unreadable.Count > 0 ? L.F("{0} mục không đọc được đã bị bỏ qua.", prepared.Unreadable.Count) : null;
    }

    internal Task WithdrawAsync(SentItemViewModel offer) => offers.WithdrawAsync(offer.OfferId);

    internal void Download(OfferItemViewModel offer) => inbox.Download(offer.Key);

    internal Task DeclineAsync(OfferItemViewModel offer) => inbox.DeclineAsync(offer.Key);

    /// <summary>"/storage/emulated/0/" before "Download/Shorekeeper": the shared storage is all the user sees of it.</summary>
    [GeneratedRegex(@"^/storage/emulated/\d+/")]
    private static partial Regex SharedStoragePrefix();

    /// <summary>"482913" → "482 913", as the desktop shows it.</summary>
    internal static string Format(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    private void RefreshPeers()
    {
        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);
        List<PeerItemViewModel> items =
        [
            .. trust.GetAll(TrustLevel.Trusted).Select(record => new PeerItemViewModel(
                record.DeviceId, record.ShownName, record.HostName, Detail(live.GetValueOrDefault(record.DeviceId), record.HostName), true,
                live.GetValueOrDefault(record.DeviceId), record.AutoAcceptMaxBytes is not null, this)),
            .. live.Values
                .Where(peer => trust.GetLevel(peer.DeviceId) == TrustLevel.Unknown)
                .Select(peer => new PeerItemViewModel(
                    peer.DeviceId, peer.DisplayName, peer.HostName, Detail(peer, peer.HostName), false, peer, false, this)),
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

    private static string Detail(PeerInfo? live, string host) => live is null ? host : $"{live.HostName} · {live.Address}";
}

/// <param name="live">Set while the peer is on the network.</param>
public sealed partial class PeerItemViewModel(
    DeviceId deviceId, string name, string host, string detail, bool isTrusted, PeerInfo? live, bool isAutoAccept, MainViewModel owner)
{
    /// <summary>Offline contacts show as computers: presence is the only place the OS is known.</summary>
    public bool IsPhone { get; } = live?.Os.Equals("android", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsOnline { get; } = live is not null;

    public bool IsBusy { get; } = live?.IsBusy == true;

    /// <summary>Where a contact is at, next to its dot (people not connected show "Chưa kết nối" instead).</summary>
    public string StatusText { get; } = live is null ? "Offline" : live.IsBusy ? L.T("Bận") : "Online";

    public string Host { get; } = host;

    /// <summary>"Tự nhận file" is on for this contact.</summary>
    public bool IsAutoAccept { get; } = isAutoAccept;

    public DeviceId DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public bool IsTrusted { get; } = isTrusted;

    public bool CanConnect { get; } = !isTrusted && live is not null;

    /// <summary>Contacts receive files; the picker is opened by the view (it needs the window).</summary>
    public bool CanSend { get; } = isTrusted;

    internal Task<string?> SendAsync(IReadOnlyList<string> locations) => owner.SendAsync(locations, [(DeviceId, null)]);

    [RelayCommand]
    private Task ConnectAsync() => owner.ConnectAsync(DeviceId, Name);

    [RelayCommand]
    private void ChooseAutoAccept() => owner.ChooseAutoAccept(this);

    [RelayCommand]
    private void Rename() => owner.AskAlias(this);

    [RelayCommand]
    private void Disconnect() => owner.ConfirmDisconnect(this);

    [RelayCommand]
    private void Block() => owner.ConfirmBlock(this);
}

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

    [RelayCommand]
    private void Close()
    {
        FindCommand.Cancel();
        owner.AddPeer = null;
    }
}

/// <summary>A question over the page: a confirmation, or a name to type when <paramref name="input"/> is set.</summary>
public sealed partial class QuestionViewModel(string title, string? message, string okText, string? input, Func<string, Task> ok, MainViewModel owner)
    : ObservableObject
{
    public string Title { get; } = title;

    public string? Message { get; } = message;

    public bool HasMessage => Message is not null;

    public string OkText { get; } = okText;

    public bool HasInput { get; } = input is not null;

    [ObservableProperty]
    public partial string Input { get; set; } = input ?? "";

    [RelayCommand]
    private async Task OkAsync()
    {
        owner.Question = null;
        await ok(Input.Trim());
    }

    [RelayCommand]
    private void Cancel() => owner.Question = null;
}

public sealed partial class OfferItemViewModel(ReceivedOffer offer, string senderName, MainViewModel owner)
{
    public OfferKey Key { get; } = new(offer.SenderId, offer.OfferId);

    public string Title { get; } = L.F("{0} gửi cho bạn", senderName);

    public string Summary { get; } = $"{TransferText.Summary(offer.Files.Select(f => f.RelativePath))} · {TransferText.Size(offer.TotalBytes)}";

    public OfferContents Contents { get; } = owner.ContentsOf(offer.OfferId, [.. offer.Files.Select(f => f.RelativePath)]);

    public string? Note { get; } = offer.Note;

    public bool HasNote => Note is not null;

    public TransferKind Kind { get; } = TransferText.KindOf([.. offer.Files.Where(f => !f.IsDirectory).Select(f => f.RelativePath)]);

    public bool IsCompleted { get; } = offer.State == InboxState.Completed;

    public double Progress { get; } = offer.TotalBytes == 0 ? 0 : 100.0 * offer.BytesReceived / offer.TotalBytes;

    public bool IsDownloading { get; } = offer.State == InboxState.Downloading;

    public string Status { get; } = offer.State switch
    {
        InboxState.New => L.T("Mới"),
        InboxState.Downloading when offer.ReconnectingUntil is not null => L.T("Mất kết nối, đang thử lại…"),
        InboxState.Downloading => L.F("Đang tải {0} / {1}", TransferText.Size(offer.BytesReceived), TransferText.Size(offer.TotalBytes)),
        InboxState.Completed => L.T("Đã tải xong"),
        InboxState.Partial => L.T("Đã tải một phần"),
        InboxState.Failed => L.F("Lỗi: {0}", offer.Error),
        InboxState.Cancelled => L.T("Đã dừng"),
        InboxState.Declined => L.T("Đã bỏ qua"),
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

    public string Title { get; } = L.F("{0} muốn kết nối", request.Name);

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

    public string Title { get; } = L.F("Kết nối với {0}", name);

    [ObservableProperty]
    public partial string Code { get; set; } = "…";

    [ObservableProperty]
    public partial string Status { get; set; } = L.T("Đang gửi yêu cầu… So mã này với mã trên máy kia.");

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

    public OfferContents Contents { get; } = owner.ContentsOf(offer.OfferId, offer.Paths);

    public string Recipients { get; } = string.Join("\n", offer.Recipients.Select(r => $"{owner.NameOf(r.DeviceId)}: {State(r.State)}"));

    public TransferKind Kind { get; } = TransferText.KindOf([.. offer.Paths.Where(p => !p.EndsWith('/'))]);

    public string Status { get; } = offer.State switch
    {
        OfferState.Open => offer.HasChangedFiles ? L.T("Đang mở · có file đã bị sửa") : L.T("Đang mở"),
        OfferState.Withdrawn => L.T("Đã thu hồi"),
        OfferState.Expired => L.T("Đã hết hạn"),
        _ => L.T("Hoàn tất"),
    };

    public bool CanWithdraw { get; } = offer.State == OfferState.Open;

    [RelayCommand]
    private Task WithdrawAsync() => owner.WithdrawAsync(this);

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

    public string Title => L.F("Gửi {0} mục cho…", locations.Count);

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
