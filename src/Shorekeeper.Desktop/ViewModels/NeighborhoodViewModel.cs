using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>The "Xóm" page: peers currently visible on the network, sorted by name.</summary>
public sealed partial class NeighborhoodViewModel : ObservableObject
{
    public NeighborhoodViewModel(PeerDirectory directory)
    {
        // Subscribe before taking the snapshot so nothing is missed; Apply is idempotent.
        directory.Changed += (_, change) => Dispatcher.UIThread.Post(() => Apply(change));
        foreach (PeerInfo peer in directory.Snapshot())
        {
            Apply(new PeerChange(PeerChangeKind.Added, peer));
        }
    }

    public ObservableCollection<PeerCardViewModel> Peers { get; } = [];

    public bool IsEmpty => Peers.Count == 0;

    public string Header => $"Đang online trong mạng ({Peers.Count})";

    private void Apply(PeerChange change)
    {
        PeerCardViewModel? card = Peers.FirstOrDefault(p => p.DeviceId == change.Peer.DeviceId);
        if (card is not null)
        {
            Peers.Remove(card);
        }

        if (change.Kind != PeerChangeKind.Removed)
        {
            card ??= new PeerCardViewModel(change.Peer.DeviceId);
            card.Update(change.Peer);
            int index = 0;
            // Culture-aware so Vietnamese names sort the way people expect.
            while (index < Peers.Count && CultureInfo.CurrentCulture.CompareInfo.Compare(Peers[index].DisplayName, card.DisplayName, CompareOptions.IgnoreCase) <= 0)
            {
                index++;
            }

            Peers.Insert(index, card);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Header));
    }
}

public sealed partial class PeerCardViewModel(DeviceId deviceId) : ObservableObject
{
    public DeviceId DeviceId { get; } = deviceId;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsStale { get; set; }

    public void Update(PeerInfo peer)
    {
        DisplayName = peer.DisplayName;
        Subtitle = $"{peer.HostName} · {peer.Address}";
        IsBusy = peer.IsBusy;
        IsStale = peer.State == PeerState.Stale;
        StatusText = IsStale ? "Mất tín hiệu…" : IsBusy ? "Bận" : "Online";
    }
}
