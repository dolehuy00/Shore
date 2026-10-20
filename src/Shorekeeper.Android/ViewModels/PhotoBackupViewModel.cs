using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Shorekeeper.Android.Platform;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.ViewModels;

/// <summary>A computer in "Gửi về": its name, whether it is online, and its host and short id to tell alike names apart.</summary>
public sealed record ComputerOption(string Name, bool IsOnline, string Detail)
{
    public string StatusText => IsOnline ? "Online" : "Offline";
}

/// <summary>
/// "Tự gửi ảnh về máy tính" in Cài đặt (docs/10-ux.md §13): which computer, what to send, and how it goes. Turning it
/// on asks for the permission to read pictures and videos.
/// </summary>
public sealed partial class PhotoBackupViewModel : ObservableObject
{
    private readonly PhotoBackupService backup;
    private readonly TrustStore trust;
    private readonly PeerDirectory directory;
    private readonly MainViewModel main;
    private IReadOnlyList<PeerRecord> computers = [];
    private bool refreshing;

    public PhotoBackupViewModel(PhotoBackupService backup, TrustStore trust, PeerDirectory directory, MainViewModel main)
    {
        this.backup = backup;
        this.trust = trust;
        this.directory = directory;
        this.main = main;
        backup.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    /// <summary>Connected computers ("Gửi về"), online ones first; phones are left out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoComputer))]
    public partial IReadOnlyList<ComputerOption> Computers { get; set; } = [];

    public bool HasNoComputer => Computers.Count == 0;

    [ObservableProperty]
    public partial int ComputerIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool CameraPhotos { get; set; }

    [ObservableProperty]
    public partial bool CameraVideos { get; set; }

    [ObservableProperty]
    public partial bool Screenshots { get; set; }

    [ObservableProperty]
    public partial bool Other { get; set; }

    /// <summary>"Đã gửi 12 ảnh, video lúc 15:30", "Sẽ gửi khi PC · MSI online"…; null while off.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!refreshing)
        {
            _ = SwitchAsync(value);
        }
    }

    partial void OnComputerIndexChanged(int value)
    {
        if (!refreshing && IsOn && value >= 0 && value < computers.Count)
        {
            backup.Enable(computers[value].DeviceId, Kinds());
        }
    }

    partial void OnCameraPhotosChanged(bool value) => KindsChanged();

    partial void OnCameraVideosChanged(bool value) => KindsChanged();

    partial void OnScreenshotsChanged(bool value) => KindsChanged();

    partial void OnOtherChanged(bool value) => KindsChanged();

    private async Task SwitchAsync(bool on)
    {
        if (!on)
        {
            backup.Disable();
            return;
        }

        try
        {
            if (ComputerIndex < 0 || ComputerIndex >= computers.Count)
            {
                main.Notice = L.T("Hãy Kết nối với máy tính trước, rồi chọn máy đó ở Gửi về.");
            }
            else if (MainActivity.Current is { } activity && await MediaPermission.RequestAsync(activity))
            {
                backup.Enable(computers[ComputerIndex].DeviceId, Kinds());
                return;
            }
        }
        catch (InvalidOperationException ex)
        {
            main.Notice = ex.Message;
        }

        Refresh();
    }

    private void KindsChanged()
    {
        if (!refreshing && IsOn)
        {
            backup.SetKinds(Kinds());
        }
    }

    private MediaKinds Kinds() =>
        (CameraPhotos ? MediaKinds.CameraPhotos : 0) | (CameraVideos ? MediaKinds.CameraVideos : 0)
        | (Screenshots ? MediaKinds.Screenshots : 0) | (Other ? MediaKinds.Other : 0);

    private void Refresh()
    {
        refreshing = true;
        PhotoBackupStatus status = backup.Status;
        HashSet<DeviceId> online = [.. directory.Snapshot().Where(p => p.State == PeerState.Online).Select(p => p.DeviceId)];
        computers = [.. trust.GetAll(TrustLevel.Trusted).Where(p => !p.IsPhone)
            .OrderBy(p => !online.Contains(p.DeviceId)).ThenBy(p => p.ShownName, StringComparer.CurrentCultureIgnoreCase)];
        List<ComputerOption> options = [.. computers.Select(p => new ComputerOption(p.ShownName, online.Contains(p.DeviceId), $"{p.HostName} · {p.DeviceId.ShortForm}"))];
        // Presence changes often; the list is replaced only when what it shows does, so an open drop-down stays put.
        if (!options.SequenceEqual(Computers))
        {
            Computers = options;
        }

        int chosen = computers.ToList().FindIndex(p => p.DeviceId == status.Target);
        ComputerIndex = chosen >= 0 ? chosen : computers.Count > 0 ? 0 : -1;
        IsOn = status.Target is not null;
        CameraPhotos = status.Kinds.HasFlag(MediaKinds.CameraPhotos);
        CameraVideos = status.Kinds.HasFlag(MediaKinds.CameraVideos);
        Screenshots = status.Kinds.HasFlag(MediaKinds.Screenshots);
        Other = status.Kinds.HasFlag(MediaKinds.Other);
        refreshing = false;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        PhotoBackupStatus status = backup.Status;
        if (status.Target is not { } target)
        {
            Status = null;
            return;
        }

        string name = trust.Get(target)?.ShownName ?? target.ShortForm;
        bool online = directory.Snapshot().Any(p => p.DeviceId == target && p.State == PeerState.Online);
        Status = status.Waiting ? L.F("Đang chờ {0} nhận đợt ảnh vừa gửi.", name)
            : !online ? L.F("Sẽ gửi khi {0} online.", name)
            : status.LastAt is { } at ? L.F("Đã gửi {0} ảnh, video lúc {1:HH:mm}.", status.LastCount, at)
            : L.T("Chưa có ảnh mới để gửi.");
    }
}
