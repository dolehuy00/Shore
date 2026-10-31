using CommunityToolkit.Mvvm.ComponentModel;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Android.ViewModels;

/// <param name="groupId">Null for contacts; for fellow members, the group the offer goes through.</param>
/// <param name="live">Set while the person is on the network.</param>
public sealed partial class PickablePerson(DeviceId deviceId, string name, string detail, string? groupId, PeerInfo? live) : ObservableObject
{
    /// <summary>Offline people are still listed: the offer waits for them (docs/06-file-transfer.md).</summary>
    public bool IsOnline { get; } = live?.State == PeerState.Online;

    public bool IsBusy { get; } = live?.IsBusy == true;

    public string StatusText { get; } = live?.State != PeerState.Online ? "Offline" : live.IsBusy ? L.T("Bận") : "Online";

    public DeviceId DeviceId { get; } = deviceId;

    public string? GroupId { get; } = groupId;

    public string Name { get; } = name;

    public string Detail { get; } = detail;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
