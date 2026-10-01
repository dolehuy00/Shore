using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>
/// The "Xóm" page (docs/10-ux.md §2): contacts (online or not) first, then everyone else currently online.
/// Blocked peers are hidden.
/// </summary>
public sealed partial class NeighborhoodViewModel : ObservableObject
{
    private readonly PeerDirectory directory;
    private readonly TrustStore trust;
    private readonly PairingService pairing;
    private readonly DialogService dialogs;
    private readonly Dictionary<DeviceId, PeerCardViewModel> cards = [];

    public NeighborhoodViewModel(PeerDirectory directory, TrustStore trust, PairingService pairing, DialogService dialogs)
    {
        this.directory = directory;
        this.trust = trust;
        this.pairing = pairing;
        this.dialogs = dialogs;

        directory.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<PeerCardViewModel> Contacts { get; } = [];

    public ObservableCollection<PeerCardViewModel> Others { get; } = [];

    public bool HasContacts => Contacts.Count > 0;

    public bool IsEmpty => Contacts.Count == 0 && Others.Count == 0;

    public string ContactsHeader => $"Đã kết nối ({Contacts.Count})";

    public string OthersHeader => $"Đang online trong mạng ({Others.Count})";

    [RelayCommand]
    private Task AddPeerAsync() => dialogs.AddPeerAsync();

    private void Refresh()
    {
        Dictionary<DeviceId, PeerInfo> live = directory.Snapshot().ToDictionary(p => p.DeviceId);

        var contacts = trust.GetAll(TrustLevel.Trusted)
            .Select(record => Card(record.DeviceId).Update(record, live.GetValueOrDefault(record.DeviceId)))
            .ToList();
        var others = live.Values
            .Where(peer => trust.GetLevel(peer.DeviceId) == TrustLevel.Unknown)
            .Select(peer => Card(peer.DeviceId).Update(null, peer))
            .ToList();

        Sync(Contacts, contacts);
        Sync(Others, others);
        OnPropertyChanged(nameof(HasContacts));
        OnPropertyChanged(nameof(IsEmpty));
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

    /// <summary>Cards are updated in place; the list is only rebuilt when membership or order changes.</summary>
    private static void Sync(ObservableCollection<PeerCardViewModel> target, List<PeerCardViewModel> desired)
    {
        // Culture-aware so Vietnamese names sort the way people expect.
        desired.Sort((x, y) => CultureInfo.CurrentCulture.CompareInfo.Compare(x.DisplayName, y.DisplayName, CompareOptions.IgnoreCase));
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
    /// (docs/10-ux.md §2). Only contacts can receive files.
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

    internal Task ToggleAutoAcceptAsync(PeerCardViewModel card) =>
        trust.SetAutoAcceptAsync(card.DeviceId, card.IsAutoAccept ? null : PeerCardViewModel.AutoAcceptLimit);

    internal async Task SendFilesAsync(PeerCardViewModel card) => await SendAsync(card, await DialogService.PickFilesAsync());

    internal async Task SendFolderAsync(PeerCardViewModel card) => await SendAsync(card, await DialogService.PickFolderAsync());

    internal Task ConnectAsync(PeerCardViewModel card) => dialogs.ConnectAsync(card.DeviceId, card.DisplayName);

    internal async Task RenameAsync(PeerCardViewModel card)
    {
        string? alias = await DialogService.PromptAsync("Đặt tên gợi nhớ", $"Tên gợi nhớ cho {card.HostName}:", card.DisplayName);
        if (alias is not null)
        {
            await trust.SetAliasAsync(card.DeviceId, alias);
        }
    }

    internal Task DisconnectAsync(PeerCardViewModel card) => pairing.DisconnectAsync(card.DeviceId);

    internal Task BlockAsync(PeerCardViewModel card) => trust.BlockAsync(card.DeviceId, card.DisplayName, card.HostName);
}

public sealed partial class PeerCardViewModel(DeviceId deviceId, NeighborhoodViewModel owner) : ObservableObject
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

    /// <summary>Stale or offline: shown dimmed with a gray dot.</summary>
    [ObservableProperty]
    public partial bool IsInactive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial bool IsTrusted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial bool IsOnline { get; set; }

    public bool CanConnect => !IsTrusted && IsOnline;

    /// <summary>Offers from a contact with "Tự nhận file" on download by themselves up to this size.</summary>
    public const long AutoAcceptLimit = 2L * 1024 * 1024 * 1024;

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
    public PeerCardViewModel Update(PeerRecord? record, PeerInfo? live)
    {
        HostName = live?.HostName ?? record?.HostName ?? "";
        DisplayName = record?.ShownName ?? live?.DisplayName ?? "";
        IsTrusted = record?.TrustLevel == TrustLevel.Trusted;
        IsAutoAccept = record?.AutoAcceptMaxBytes is not null;
        IsOnline = live is not null;
        IsBusy = live?.IsBusy == true;
        IsInactive = live is null || live.State == PeerState.Stale;
        Subtitle = live is null ? HostName : $"{HostName} · {live.Address}";
        StatusText = live is null ? "Offline"
            : live.State == PeerState.Stale ? "Mất tín hiệu…"
            : IsBusy ? "Bận"
            : "Online";
        return this;
    }

    [RelayCommand]
    private Task ConnectAsync() => owner.ConnectAsync(this);

    [RelayCommand]
    private Task SendFilesAsync() => owner.SendFilesAsync(this);

    [RelayCommand]
    private Task ToggleAutoAcceptAsync() => owner.ToggleAutoAcceptAsync(this);

    [RelayCommand]
    private Task SendFolderAsync() => owner.SendFolderAsync(this);

    [RelayCommand]
    private Task RenameAsync() => owner.RenameAsync(this);

    [RelayCommand]
    private Task DisconnectAsync() => owner.DisconnectAsync(this);

    [RelayCommand]
    private Task BlockAsync() => owner.BlockAsync(this);
}
