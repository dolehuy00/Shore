using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Trust;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>
/// The "Xóm" page (docs/10-ux.md §2): contacts (online or not) first, then everyone else currently online.
/// Blocked peers are hidden. With Active Directory, people are ordered by department and show their picture.
/// </summary>
public sealed partial class NeighborhoodViewModel : ObservableObject
{
    private readonly PeerDirectory directory;
    private readonly TrustStore trust;
    private readonly GroupStore groups;
    private readonly PairingService pairing;
    private readonly DialogService dialogs;
    private readonly PeerAvatars avatars;
    private readonly SettingsService settings;
    private readonly Dictionary<DeviceId, PeerCardViewModel> cards = [];

    public NeighborhoodViewModel(
        PeerDirectory directory, TrustStore trust, GroupStore groups, PairingService pairing, DialogService dialogs, PeerAvatars avatars,
        SettingsService settings)
    {
        this.directory = directory;
        this.trust = trust;
        this.groups = groups;
        this.pairing = pairing;
        this.dialogs = dialogs;
        this.avatars = avatars;
        this.settings = settings;

        directory.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        groups.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<PeerCardViewModel> Contacts { get; } = [];

    public ObservableCollection<PeerCardViewModel> Others { get; } = [];

    /// <summary>Contacts and everyone online (for the Ctrl+K search).</summary>
    public IReadOnlyList<PeerCardViewModel> Everyone { get; private set; } = [];

    public bool HasContacts => Contacts.Count > 0;

    public bool IsEmpty => Contacts.Count == 0 && Others.Count == 0;

    public bool HasOthers => Others.Count > 0;

    public string ContactsHeader => L.F("Đã kết nối ({0})", Contacts.Count);

    public string OthersHeader => L.F("Đang online trong mạng ({0})", Others.Count);

    [RelayCommand]
    private Task AddPeerAsync() => dialogs.AddPeerAsync();

    private void Refresh()
    {
        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);
        Group[] joined = [.. groups.GetAll().Where(g => g.Role != GroupRole.Pending)];

        var contacts = trust.GetAll(TrustLevel.Trusted)
            .Select(record => Show(Card(record.DeviceId).Update(record, live.GetValueOrDefault(record.DeviceId), null), live.GetValueOrDefault(record.DeviceId)))
            .ToList();
        var others = live.Values
            .Where(peer => trust.GetLevel(peer.DeviceId) == TrustLevel.Unknown)
            .Select(peer => Show(Card(peer.DeviceId).Update(null, peer, joined.FirstOrDefault(g => g.Contains(peer.DeviceId))), peer))
            .ToList();
        Everyone = [.. contacts, .. others];

        Sync(Contacts, contacts);
        Sync(Others, others);
        OnPropertyChanged(nameof(HasContacts));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasOthers));
        OnPropertyChanged(nameof(ContactsHeader));
        OnPropertyChanged(nameof(OthersHeader));
    }

    private PeerCardViewModel Card(DeviceId id)
    {
        if (!cards.TryGetValue(id, out PeerCardViewModel? card))
        {
            card = new PeerCardViewModel(id, this);
            cards[id] = card;
        }

        return card;
    }

    /// <summary>Loads the peer's picture when it announces a new one.</summary>
    private PeerCardViewModel Show(PeerCardViewModel card, PeerInfo? live)
    {
        if (live?.AvatarTag is { } tag && tag != card.AvatarTag)
        {
            card.AvatarTag = tag;
            _ = LoadAvatarAsync(card, live);
        }

        return card;
    }

    private async Task LoadAvatarAsync(PeerCardViewModel card, PeerInfo live)
    {
        if (await avatars.GetAsync(live) is not { } photo || card.AvatarTag != live.AvatarTag)
        {
            return;
        }

        try
        {
            card.Avatar = new Bitmap(new MemoryStream(photo));
        }
        catch (Exception)
        {
            // Not a picture Avalonia can read: keep the plain card.
        }
    }

    /// <summary>Cards are updated in place; the list is only rebuilt when membership or order changes.</summary>
    private static void Sync(ObservableCollection<PeerCardViewModel> target, List<PeerCardViewModel> desired)
    {
        // By department (people without one last), then name; culture-aware so Vietnamese sorts the way people expect.
        CompareInfo compare = CultureInfo.CurrentCulture.CompareInfo;
        desired.Sort((x, y) =>
            x.Department is null != y.Department is null ? (x.Department is null ? 1 : -1)
            : compare.Compare(x.Department, y.Department, CompareOptions.IgnoreCase) is var byDepartment and not 0 ? byDepartment
            : compare.Compare(x.DisplayName, y.DisplayName, CompareOptions.IgnoreCase));
        if (target.SequenceEqual(desired))
        {
            return;
        }

        target.Clear();
        foreach (PeerCardViewModel card in desired)
        {
            target.Add(card);
        }
    }

    /// <summary>
    /// Sends to the card, or to every selected card when it is part of a Ctrl+click selection
    /// (docs/10-ux.md §2). Contacts can receive files, and so can fellow group members (one at a time,
    /// as the offer belongs to the group they share).
    /// </summary>
    public async Task SendAsync(PeerCardViewModel card, IReadOnlyList<string> paths)
    {
        PeerCardViewModel[] targets = card.IsSelected
            ? [.. Contacts.Where(c => c.IsSelected)]
            : [card];
        foreach (PeerCardViewModel selected in Contacts.Where(c => c.IsSelected))
        {
            selected.IsSelected = false;
        }

        if (!card.IsTrusted && card.SharedGroupId is { } groupId)
        {
            await dialogs.SendAsync(paths, [(card.DeviceId, card.DisplayName)], groupId);
            return;
        }

        await dialogs.SendAsync(paths, [.. targets.Where(t => t.IsTrusted).Select(t => (t.DeviceId, t.DisplayName))]);
    }

    /// <summary>Ctrl+V: sends what is on the clipboard to the selected contacts.</summary>
    /// <returns>False when no contact is selected.</returns>
    public async Task<bool> SendToSelectedAsync(IReadOnlyList<string> paths)
    {
        PeerCardViewModel? first = Contacts.FirstOrDefault(c => c.IsSelected);
        if (first is null)
        {
            return false;
        }

        await SendAsync(first, paths);
        return true;
    }

    /// <summary>Plain click: this card becomes the only selected one; Ctrl+click adds or removes it.</summary>
    public void Select(PeerCardViewModel card, bool toggle)
    {
        if (!card.IsTrusted)
        {
            return;
        }

        if (toggle)
        {
            card.IsSelected = !card.IsSelected;
            return;
        }

        foreach (PeerCardViewModel other in Contacts)
        {
            other.IsSelected = other == card;
        }
    }

    internal Task ChooseAutoAcceptAsync(PeerCardViewModel card) =>
        DialogService.ChooseAutoAcceptAsync(
            card.DisplayName, trust.Get(card.DeviceId)?.AutoAcceptMaxBytes, settings.Current.AutoAcceptLimitGb,
            maxBytes => trust.SetAutoAcceptAsync(card.DeviceId, maxBytes));

    internal async Task SendFilesAsync(PeerCardViewModel card) => await SendAsync(card, await DialogService.PickFilesAsync());

    internal async Task SendFolderAsync(PeerCardViewModel card) => await SendAsync(card, await DialogService.PickFolderAsync());

    internal Task ConnectAsync(PeerCardViewModel card) => dialogs.ConnectAsync(card.DeviceId, card.DisplayName);

    internal async Task RenameAsync(PeerCardViewModel card)
    {
        string? alias = await DialogService.PromptAsync(L.T("Đặt tên gợi nhớ"), L.F("Tên gợi nhớ cho {0}:", card.HostName), card.DisplayName);
        if (alias is not null)
        {
            await trust.SetAliasAsync(card.DeviceId, alias);
        }
    }

    internal Task DisconnectAsync(PeerCardViewModel card) => pairing.DisconnectAsync(card.DeviceId);

    internal Task BlockAsync(PeerCardViewModel card) => trust.BlockAsync(card.DeviceId, card.DisplayName, card.HostName);
}

public sealed partial class PeerCardViewModel(DeviceId deviceId, NeighborhoodViewModel owner) : ObservableObject, IFileDropTarget
{
    public DeviceId DeviceId { get; } = deviceId;

    public string HostName { get; private set; } = "";

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Android phone (from presence); computers otherwise. Offline contacts keep what was last seen.</summary>
    [ObservableProperty]
    public partial bool IsPhone { get; set; }

    /// <summary>Stale or offline: shown dimmed with a gray dot.</summary>
    [ObservableProperty]
    public partial bool IsInactive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanSend), nameof(CanDrop))]
    public partial bool IsTrusted { get; set; }

    /// <summary>A group we are both in, for peers that are not contacts: they can still receive files.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(CanDrop), nameof(HasGroup))]
    public partial string? SharedGroupId { get; set; }

    [ObservableProperty]
    public partial string GroupText { get; set; } = "";

    /// <summary>From the company directory (a claim, like the name).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDepartment))]
    public partial string? Department { get; set; }

    public bool HasDepartment => Department is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    public partial Bitmap? Avatar { get; set; }

    public bool HasAvatar => Avatar is not null;

    /// <summary>Version of the picture shown or being loaded.</summary>
    internal string? AvatarTag { get; set; }

    public bool HasGroup => !IsTrusted && SharedGroupId is not null;

    public bool CanSend => IsTrusted || SharedGroupId is not null;

    public bool CanDrop => CanSend;

    public Task DropAsync(IReadOnlyList<string> paths) => owner.SendAsync(this, paths);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial bool IsOnline { get; set; }

    public bool CanConnect => !IsTrusted && IsOnline;

    [ObservableProperty]
    public partial bool IsAutoAccept { get; set; }

    /// <summary>Picked with Ctrl+click; dropping files on any selected card sends to all of them.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Files are being dragged over this card.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    /// <param name="record">Set for contacts.</param>
    /// <param name="live">Set while the peer is visible on the network.</param>
    /// <param name="sharedGroup">A group we are both in (looked up for peers that are not contacts).</param>
    public PeerCardViewModel Update(PeerRecord? record, PeerInfo? live, Group? sharedGroup)
    {
        SharedGroupId = sharedGroup?.Id;
        GroupText = sharedGroup is null ? "" : L.F("Cùng nhóm {0}", sharedGroup.Name);
        HostName = live?.HostName ?? record?.HostName ?? "";
        DisplayName = record?.ShownName ?? live?.DisplayName ?? "";
        IsTrusted = record?.TrustLevel == TrustLevel.Trusted;
        IsAutoAccept = record?.AutoAcceptMaxBytes is not null;
        IsOnline = live is not null;
        // Offline contacts keep what was last seen.
        Department = live is null ? Department : live.Department;
        IsPhone = live is null ? IsPhone : live.Os.Equals("android", StringComparison.OrdinalIgnoreCase);
        IsBusy = live?.IsBusy == true;
        IsInactive = live is null || live.State == PeerState.Stale;
        Subtitle = live is null ? HostName
            : live.Source == PeerSource.Bridge ? L.F("{0} · {1} · qua cầu nối", HostName, live.Address)
            : $"{HostName} · {live.Address}";
        StatusText = live is null ? "Offline"
            : live.State == PeerState.Stale ? L.T("Mất tín hiệu…")
            : IsBusy ? L.T("Bận")
            : "Online";
        return this;
    }

    [RelayCommand]
    private Task ConnectAsync() => owner.ConnectAsync(this);

    [RelayCommand]
    private Task SendFilesAsync() => owner.SendFilesAsync(this);

    [RelayCommand]
    private Task ChooseAutoAcceptAsync() => owner.ChooseAutoAcceptAsync(this);

    [RelayCommand]
    private Task SendFolderAsync() => owner.SendFolderAsync(this);

    [RelayCommand]
    private Task RenameAsync() => owner.RenameAsync(this);

    [RelayCommand]
    private Task DisconnectAsync() => owner.DisconnectAsync(this);

    [RelayCommand]
    private Task BlockAsync() => owner.BlockAsync(this);
}
