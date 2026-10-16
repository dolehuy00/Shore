using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Settings;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Desktop.ViewModels;

public sealed record SettingChoice(int Value, string Label);

/// <param name="Code">"vi", "en", or null for the system language.</param>
public sealed record LanguageChoice(string? Code, string Label);

/// <summary>"Cài đặt → Gửi & nhận" and "Máy ở mạng khác" (docs/10-ux.md §9). Settings locked by IT policy are read-only.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly FilePickerFileType BackupFileType = new(L.T("Bản sao lưu Shorekeeper")) { Patterns = ["*" + IdentityBackup.FileExtension] };

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
        new(1, L.T("1 giờ")), new(6, L.T("6 giờ")), new(24, L.T("1 ngày")), new(72, L.T("3 ngày")), new(168, L.T("7 ngày")),
    ];

    public IReadOnlyList<SettingChoice> ReconnectChoices { get; } =
    [
        new(15, L.T("15 giây")), new(30, L.T("30 giây")), new(60, L.T("1 phút")), new(120, L.T("2 phút")), new(300, L.T("5 phút")),
    ];

    /// <summary>Each language named in itself, so it can be found whatever the current one is.</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } =
    [
        new(null, L.T("Theo hệ điều hành")), new(L.Vietnamese, "Tiếng Việt"), new(L.English, "English"),
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

    [ObservableProperty]
    public partial LanguageChoice Language { get; set; }

    public string HotkeyText => L.F("Phím tắt {0} mở cửa sổ Gửi nhanh", quickSend.HotkeyText);

    /// <summary>Null unless the shortcut is on but another program already took it.</summary>
    public string? HotkeyWarning => quickSend.IsHotkeyTaken
        ? L.F("{0} đang được ứng dụng khác dùng nên chưa bật được. Vẫn mở Gửi nhanh được từ icon ở khay hệ thống.", quickSend.HotkeyText)
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
        string title = L.T("Sao lưu danh tính");
        string? password = await DialogService.PromptAsync(title, L.F("Đặt mật khẩu cho bản sao lưu (ít nhất {0} ký tự):", IdentityBackup.MinPasswordLength), "", isPassword: true, okText: L.T("Tiếp"));
        if (password is null)
        {
            return;
        }

        if (password.Length < IdentityBackup.MinPasswordLength)
        {
            await DialogService.ChooseAsync(title, L.F("Mật khẩu phải có ít nhất {0} ký tự.", IdentityBackup.MinPasswordLength), [L.T("Đóng")]);
            return;
        }

        if (await DialogService.PromptAsync(title, L.T("Nhập lại mật khẩu:"), "", isPassword: true, okText: L.T("Tiếp")) is not { } again)
        {
            return;
        }

        if (again != password)
        {
            await DialogService.ChooseAsync(title, L.T("Hai lần nhập mật khẩu không khớp."), [L.T("Đóng")]);
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
            await DialogService.ChooseAsync(title, L.F("Không ghi được file: {0}", ex.Message), [L.T("Đóng")]);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            await DialogService.ChooseAsync(title, L.T("Không có quyền ghi vào thư mục đã chọn."), [L.T("Đóng")]);
            return;
        }

        await DialogService.ChooseAsync(
            L.T("Đã sao lưu"),
            L.T("Cất file và mật khẩu ở nơi an toàn: ai có cả hai có thể giả làm máy này.\n\nKhi cài lại Windows hoặc chuyển sang máy mới, vào Cài đặt → Khôi phục để giữ nguyên danh tính, liên hệ và nhóm."),
            [L.T("Đóng")]);
    }

    /// <summary>"Khôi phục…": the backup replaces the identity, contacts and groups at the next start.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        string title = L.T("Khôi phục danh tính");
        if (await DialogService.PickFileAsync(title, BackupFileType) is not { } path
            || await DialogService.PromptAsync(title, L.T("Mật khẩu của bản sao lưu:"), "", isPassword: true, okText: L.T("Mở")) is not { } password)
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
            await DialogService.ChooseAsync(title, L.T("Sai mật khẩu, hoặc file đã bị thay đổi."), [L.T("Đóng")]);
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await DialogService.ChooseAsync(title, L.T("Không đọc được: file này không phải bản sao lưu Shorekeeper, hoặc đã hỏng."), [L.T("Đóng")]);
            return;
        }

        BackupSummary summary = opened.Summary;
        int choice = await DialogService.ChooseAsync(
            title,
            L.F("Khôi phục danh tính {0} ({1}, sao lưu lúc {2:HH:mm dd/MM/yyyy}) với {3} liên hệ và {4} nhóm?\n\n",
                summary.DeviceId.ShortForm, summary.DisplayName, summary.CreatedAt.ToLocalTime(), summary.Contacts, summary.Groups)
            + L.F("Danh tính hiện tại ({0}) cùng liên hệ và nhóm đang có sẽ được thay. Shorekeeper sẽ khởi động lại.\n\n", device.DeviceId.ShortForm)
            + L.T("Đừng chạy cùng một danh tính trên hai máy cùng lúc."),
            [L.T("Khôi phục và khởi động lại"), L.T("Hủy")]);
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
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            StatusText = L.T("Tên hiển thị không được để trống.");
            return;
        }

        bool languageChanged = Language.Code != settings.User.Language;
        EffectiveSettings saved = settings.Update(current => current with
        {
            DisplayName = DisplayName.Trim(),
            DownloadDirectory = IsDownloadDirectoryLocked ? current.DownloadDirectory : DownloadDirectory,
            CreateSenderSubfolders = CreateSenderSubfolders,
            OfferLifetimeHours = OfferLifetime.Value,
            ReconnectWindowSeconds = IsReconnectWindowLocked ? current.ReconnectWindowSeconds : ReconnectWindow.Value,
            AlwaysSnapshotBeforeSend = AlwaysSnapshotBeforeSend,
            QuickSendHotkey = QuickSendHotkey,
            Language = Language.Code,
        });
        Load(saved);
        StatusText = saved.OfferLifetime < TimeSpan.FromHours(OfferLifetime.Value)
            ? L.T("Đã lưu. Quản trị viên giới hạn thời hạn mở offer ngắn hơn lựa chọn của bạn.")
            : L.T("Đã lưu.");

        // Text is translated when it is created, so a new language needs a fresh start.
        if (languageChanged
            && await DialogService.ChooseAsync(L.T("Ngôn ngữ"), L.T("Ngôn ngữ mới có hiệu lực sau khi khởi động lại Shorekeeper."), [L.T("Khởi động lại"), L.T("Để sau")]) == 0)
        {
            App.Restart();
        }
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
        NetworkStatusText = L.T("Đã lưu.");
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
        Language = LanguageChoices.FirstOrDefault(c => c.Code == settings.User.Language) ?? LanguageChoices[0];
        OfferLifetime = Closest(OfferLifetimeChoices, settings.User.OfferLifetimeHours ?? (int)current.OfferLifetime.TotalHours);
        ReconnectWindow = Closest(ReconnectChoices, (int)current.ReconnectWindow.TotalSeconds);
        IsDownloadDirectoryLocked = current.IsLocked(nameof(ShorekeeperSettings.DownloadDirectory));
        IsReconnectWindowLocked = current.IsLocked(nameof(ShorekeeperSettings.ReconnectWindowSeconds));
    }

    private static SettingChoice Closest(IReadOnlyList<SettingChoice> choices, int value) =>
        choices.MinBy(c => Math.Abs(c.Value - value))!;
}
