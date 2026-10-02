using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Settings;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Desktop.ViewModels;

public sealed record SettingChoice(int Value, string Label);

/// <summary>"Cài đặt → Gửi & nhận" and "Máy ở mạng khác" (docs/10-ux.md §9). Settings locked by IT policy are read-only.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService settings;

    public SettingsViewModel(SettingsService settings)
    {
        this.settings = settings;
        Load(settings.Current);
        LoadNetwork(settings.Current);
    }

    public IReadOnlyList<SettingChoice> OfferLifetimeChoices { get; } =
    [
        new(1, "1 giờ"), new(6, "6 giờ"), new(24, "1 ngày"), new(72, "3 ngày"), new(168, "7 ngày"),
    ];

    public IReadOnlyList<SettingChoice> ReconnectChoices { get; } =
    [
        new(15, "15 giây"), new(30, "30 giây"), new(60, "1 phút"), new(120, "2 phút"), new(300, "5 phút"),
    ];

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string DownloadDirectory { get; set; } = "";

    [ObservableProperty]
    public partial bool CreateSenderSubfolders { get; set; }

    [ObservableProperty]
    public partial SettingChoice OfferLifetime { get; set; }

    [ObservableProperty]
    public partial SettingChoice ReconnectWindow { get; set; }

    [ObservableProperty]
    public partial bool AlwaysSnapshotBeforeSend { get; set; }

    [ObservableProperty]
    public partial bool IsDownloadDirectoryLocked { get; set; }

    [ObservableProperty]
    public partial bool IsReconnectWindowLocked { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial bool EnableBridge { get; set; }

    /// <summary>One host name or IP per line.</summary>
    [ObservableProperty]
    public partial string BridgeAddresses { get; set; } = "";

    /// <summary>One CIDR range per line.</summary>
    [ObservableProperty]
    public partial string ProbeSubnets { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBridgeLocked { get; set; }

    [ObservableProperty]
    public partial bool IsBridgeAddressesLocked { get; set; }

    [ObservableProperty]
    public partial bool IsProbeSubnetsLocked { get; set; }

    [ObservableProperty]
    public partial string? NetworkStatusText { get; set; }

    [RelayCommand]
    private async Task PickDownloadDirectoryAsync()
    {
        IReadOnlyList<string> picked = await DialogService.PickFolderAsync();
        if (picked.Count > 0)
        {
            DownloadDirectory = picked[0];
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            StatusText = "Tên hiển thị không được để trống.";
            return;
        }

        EffectiveSettings saved = settings.Update(current => current with
        {
            DisplayName = DisplayName.Trim(),
            DownloadDirectory = IsDownloadDirectoryLocked ? current.DownloadDirectory : DownloadDirectory,
            CreateSenderSubfolders = CreateSenderSubfolders,
            OfferLifetimeHours = OfferLifetime.Value,
            ReconnectWindowSeconds = IsReconnectWindowLocked ? current.ReconnectWindowSeconds : ReconnectWindow.Value,
            AlwaysSnapshotBeforeSend = AlwaysSnapshotBeforeSend,
        });
        Load(saved);
        StatusText = saved.OfferLifetime < TimeSpan.FromHours(OfferLifetime.Value)
            ? "Đã lưu. Quản trị viên giới hạn thời hạn mở offer ngắn hơn lựa chọn của bạn."
            : "Đã lưu.";
    }

    [RelayCommand]
    private void SaveNetwork()
    {
        string[] subnets = Lines(ProbeSubnets);
        foreach (string subnet in subnets)
        {
            if (SubnetScanner.ParseRange(subnet, out string? error) is null)
            {
                NetworkStatusText = error;
                return;
            }
        }

        EffectiveSettings saved = settings.Update(current => current with
        {
            EnableBridge = IsBridgeLocked ? current.EnableBridge : EnableBridge,
            BridgeAddresses = IsBridgeAddressesLocked ? current.BridgeAddresses : Lines(BridgeAddresses),
            ProbeSubnets = IsProbeSubnetsLocked ? current.ProbeSubnets : subnets,
        });
        LoadNetwork(saved);
        NetworkStatusText = "Đã lưu.";
    }

    private void LoadNetwork(EffectiveSettings current)
    {
        EnableBridge = current.BridgeEnabled;
        BridgeAddresses = string.Join(Environment.NewLine, current.BridgeAddresses);
        ProbeSubnets = string.Join(Environment.NewLine, current.ProbeSubnets);
        IsBridgeLocked = current.IsLocked(nameof(ShorekeeperSettings.EnableBridge));
        IsBridgeAddressesLocked = current.IsLocked(nameof(ShorekeeperSettings.BridgeAddresses));
        IsProbeSubnetsLocked = current.IsLocked(nameof(ShorekeeperSettings.ProbeSubnets));
    }

    /// <summary>Non-empty lines; commas and semicolons separate too, as people paste lists.</summary>
    private static string[] Lines(string text) =>
        text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Load(EffectiveSettings current)
    {
        DisplayName = current.DisplayName;
        DownloadDirectory = current.DownloadDirectory;
        CreateSenderSubfolders = current.CreateSenderSubfolders;
        AlwaysSnapshotBeforeSend = current.AlwaysSnapshotBeforeSend;
        OfferLifetime = Closest(OfferLifetimeChoices, settings.User.OfferLifetimeHours ?? (int)current.OfferLifetime.TotalHours);
        ReconnectWindow = Closest(ReconnectChoices, (int)current.ReconnectWindow.TotalSeconds);
        IsDownloadDirectoryLocked = current.IsLocked(nameof(ShorekeeperSettings.DownloadDirectory));
        IsReconnectWindowLocked = current.IsLocked(nameof(ShorekeeperSettings.ReconnectWindowSeconds));
    }

    private static SettingChoice Closest(IReadOnlyList<SettingChoice> choices, int value) =>
        choices.MinBy(c => Math.Abs(c.Value - value))!;
}
