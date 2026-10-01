using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

public sealed record BlockedPeerItem(string Name, string HostName, IAsyncRelayCommand UnblockCommand);

public sealed record ManualTargetItem(string Target, IAsyncRelayCommand RemoveCommand);

/// <summary>"Cài đặt": blocked peers and machines added by hand — the only places to undo either.</summary>
public sealed class BlockedPeersViewModel
{
    private readonly TrustStore trust;

    public BlockedPeersViewModel(TrustStore trust)
    {
        this.trust = trust;
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<BlockedPeerItem> Items { get; } = [];

    public ObservableCollection<ManualTargetItem> ManualTargets { get; } = [];

    private void Refresh()
    {
        Items.Clear();
        foreach (PeerRecord peer in trust.GetAll(TrustLevel.Blocked).OrderBy(p => p.ShownName, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(new BlockedPeerItem(peer.ShownName, peer.HostName, new AsyncRelayCommand(() => trust.ForgetAsync(peer.DeviceId))));
        }

        ManualTargets.Clear();
        foreach (string target in trust.GetManualTargets())
        {
            ManualTargets.Add(new ManualTargetItem(target, new AsyncRelayCommand(() => trust.RemoveManualTargetAsync(target))));
        }
    }
}
