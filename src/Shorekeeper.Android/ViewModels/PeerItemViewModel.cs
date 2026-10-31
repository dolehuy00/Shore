using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Android.ViewModels;

/// <param name="live">Set while the peer is on the network.</param>
public sealed partial class PeerItemViewModel(
    DeviceId deviceId, string name, string host, string detail, bool isTrusted, PeerInfo? live, bool isAutoAccept, MainViewModel owner)
{
    /// <summary>Offline contacts show as computers: presence is the only place the OS is known.</summary>
    /// <summary>As it says on the network or, while offline, as it last said.</summary>
    public bool IsPhone { get; init; } = live?.Os.Equals("android", StringComparison.OrdinalIgnoreCase) == true;

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
    private void SendText() => owner.AskText(this);

    [RelayCommand]
    private void Message() => owner.OpenChat(DeviceId);

    [RelayCommand]
    private void Disconnect() => owner.ConfirmDisconnect(this);

    [RelayCommand]
    private void Block() => owner.ConfirmBlock(this);
}
