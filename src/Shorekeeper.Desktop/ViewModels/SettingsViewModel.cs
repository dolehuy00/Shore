using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Settings;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Desktop.ViewModels;

public sealed record SettingChoice(int Value, string Label);

/// <summary>"Cài đặt → Gửi & nhận" and "Máy ở mạng khác" (docs/10-ux.md §9). Settings locked by IT policy are read-only.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly FilePickerFileType BackupFileType = new("Bản sao lưu Shorekeeper") { Patterns = ["*" + IdentityBackup.FileExtension] };

    private readonly SettingsService settings;
    private readonly QuickSendService quickSend;
    private readonly IdentityBackup backup;
    private readonly LocalDevice device;

    public SettingsViewModel(SettingsService settings, QuickSendService quickSend, IdentityBackup backup, LocalDevice device)
    {
        this.settings = settings;
        this.quickSend = quickSend;
        this.backup = backup;
        this.device = device;
        quickSend.HotkeyChanged += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(HotkeyWarning)));
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
    public partial bool QuickSendHotkey { get; set; }

    public string HotkeyText => $"Phím tắt {quickSend.HotkeyText} mở cửa sổ Gửi nhanh";

    /// <summary>Null unless the shortcut is on but another program already took it.</summary>
    public string? HotkeyWarning => quickSend.IsHotkeyTaken
        ? $"{quickSend.HotkeyText} đang được ứng dụng khác dùng nên chưa bật được. Vẫn mở Gửi nhanh được từ icon ở khay hệ thống."
        : null;

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

    /// <summary>"Sao lưu danh tính…" (docs/04-identity-security.md §1.1): password twice, then where to save.</summary>
    [RelayCommand]
    private async Task BackupAsync()
    {
        const string title = "Sao lưu danh tính";
        string? password = await DialogService.PromptAsync(title, $"Đặt mật khẩu cho bản sao lưu (ít nhất {IdentityBackup.MinPasswordLength} ký tự):", "", isPassword: true, okText: "Tiếp");
        if (password is null)
        {
            return;
        }

        if (password.Length < IdentityBackup.MinPasswordLength)
        {
            await DialogService.ChooseAsync(title, $"Mật khẩu phải có ít nhất {IdentityBackup.MinPasswordLength} ký tự.", ["Đóng"]);
            return;
        }

        if (await DialogService.PromptAsync(title, "Nhập lại mật khẩu:", "", isPassword: true, okText: "Tiếp") is not { } again)
        {
            return;
        }

        if (again != password)
        {
            await DialogService.ChooseAsync(title, "Hai lần nhập mật khẩu không khớp.", ["Đóng"]);
            return;
        }

        string name = $"Shorekeeper-{device.DeviceId.ShortForm}-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{IdentityBackup.FileExtension}";
        if (await DialogService.PickSaveFileAsync(title, name, BackupFileType) is not { } path)
        {
            return;
        }

        try
        {
            await Task.Run(() => backup.ExportAsync(path, password, device.DisplayName));
        }
        catch (IOException ex)
        {
            await DialogService.ChooseAsync(title, $"Không ghi được file: {ex.Message}", ["Đóng"]);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            await DialogService.ChooseAsync(title, "Không có quyền ghi vào thư mục đã chọn.", ["Đóng"]);
            return;
        }

        await DialogService.ChooseAsync(
            "Đã sao lưu",
            "Cất file và mật khẩu ở nơi an toàn: ai có cả hai có thể giả làm máy này.\n\nKhi cài lại Windows hoặc chuyển sang máy mới, vào Cài đặt → Khôi phục để giữ nguyên danh tính, liên hệ và nhóm.",
            ["Đóng"]);
    }

    /// <summary>"Khôi phục…": the backup replaces the identity, contacts and groups at the next start.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        const string title = "Khôi phục danh tính";
        if (await DialogService.PickFileAsync(title, BackupFileType) is not { } path
            || await DialogService.PromptAsync(title, "Mật khẩu của bản sao lưu:", "", isPassword: true, okText: "Mở") is not { } password)
        {
            return;
        }

        OpenedBackup opened;
        try
        {
            opened = await Task.Run(async () => IdentityBackup.Open(await File.ReadAllBytesAsync(path), password));
        }
        catch (CryptographicException)
        {
            await DialogService.ChooseAsync(title, "Sai mật khẩu, hoặc file đã bị thay đổi.", ["Đóng"]);
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await DialogService.ChooseAsync(title, "Không đọc được: file này không phải bản sao lưu Shorekeeper, hoặc đã hỏng.", ["Đóng"]);
            return;
        }

        BackupSummary summary = opened.Summary;
        int choice = await DialogService.ChooseAsync(
            title,
            $"Khôi phục danh tính {summary.DeviceId.ShortForm} ({summary.DisplayName}, sao lưu lúc {summary.CreatedAt.ToLocalTime():HH:mm dd/MM/yyyy}) "
            + $"với {summary.Contacts} liên hệ và {summary.Groups} nhóm?\n\n"
            + $"Danh tính hiện tại ({device.DeviceId.ShortForm}) cùng liên hệ và nhóm đang có sẽ được thay. Shorekeeper sẽ khởi động lại.\n\n"
            + "Đừng chạy cùng một danh tính trên hai máy cùng lúc.",
            ["Khôi phục và khởi động lại", "Hủy"]);
        if (choice != 0)
        {
            return;
        }

        backup.StageRestore(opened);
        App.Restart();
    }

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
            QuickSendHotkey = QuickSendHotkey,
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
        QuickSendHotkey = current.QuickSendHotkey;
        OfferLifetime = Closest(OfferLifetimeChoices, settings.User.OfferLifetimeHours ?? (int)current.OfferLifetime.TotalHours);
        ReconnectWindow = Closest(ReconnectChoices, (int)current.ReconnectWindow.TotalSeconds);
        IsDownloadDirectoryLocked = current.IsLocked(nameof(ShorekeeperSettings.DownloadDirectory));
        IsReconnectWindowLocked = current.IsLocked(nameof(ShorekeeperSettings.ReconnectWindowSeconds));
    }

    private static SettingChoice Closest(IReadOnlyList<SettingChoice> choices, int value) =>
        choices.MinBy(c => Math.Abs(c.Value - value))!;
}
