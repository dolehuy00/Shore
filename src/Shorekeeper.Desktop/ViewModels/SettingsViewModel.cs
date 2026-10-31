using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Settings;
using Shorekeeper.UI.Localization;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;
using Shorekeeper.UI.ViewModels;

namespace Shorekeeper.Desktop.ViewModels;

public sealed record SettingChoice(int Value, string Label);

/// <summary>"Cài đặt → Gửi & nhận" and "Máy ở mạng khác" (docs/10-ux.md §9). Settings locked by IT policy are read-only.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly FilePickerFileType BackupFileType = new(L.T("Bản sao lưu Shorekeeper")) { Patterns = ["*" + IdentityBackup.FileExtension] };

    private readonly IDialogs dialogs;
    private readonly SettingsService settings;
    private readonly QuickSendService quickSend;
    private readonly IdentityBackup backup;
    private readonly LocalDevice device;

    public SettingsViewModel(SettingsService settings, QuickSendService quickSend, IdentityBackup backup, LocalDevice device, IDialogs dialogs)
    {
        this.dialogs = dialogs;
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

    /// <summary>"Tự nhận file tối đa" (GB) for contacts that follow Settings.</summary>
    [ObservableProperty]
    public partial decimal? AutoAcceptLimitGb { get; set; }

    [ObservableProperty]
    public partial bool QuickSendHotkey { get; set; }

    /// <summary>"Ctrl+Alt+S"; saved with the rest.</summary>
    [ObservableProperty]
    public partial string QuickSendShortcut { get; set; } = "";

    /// <summary>The shortcut button waits for keys; the current shortcut is let go meanwhile.</summary>
    [ObservableProperty]
    public partial bool IsRecordingShortcut { get; set; }

    [ObservableProperty]
    public partial LanguageChoice Language { get; set; }

    /// <summary>"Giao diện": saved and shown as soon as it is picked, without "Lưu".</summary>
    [ObservableProperty]
    public partial ThemeChoice Theme { get; set; }

    partial void OnThemeChanged(ThemeChoice value)
    {
        ThemeChoice.Apply(value.Code);
        if (value.Code != settings.User.Theme)
        {
            settings.Update(s => s with { Theme = value.Code });
        }
    }

    /// <summary>Null unless the shortcut is on but another program already took it.</summary>
    public string? HotkeyWarning => quickSend.IsHotkeyTaken
        ? L.F("{0} đang được ứng dụng khác dùng nên chưa bật được. Chọn phím tắt khác, hoặc mở Gửi nhanh từ icon ở khay hệ thống.", settings.Current.QuickSendShortcut)
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

    /// <summary>"Sao lưu danh tính…" (docs/04-identity-security.md §1.1): password twice in one dialog, then where to save.</summary>
    [RelayCommand]
    private async Task BackupAsync()
    {
        string title = L.T("Sao lưu danh tính");
        if (await dialogs.PromptPasswordAsync(
                title, L.F("Đặt mật khẩu cho bản sao lưu (ít nhất {0} ký tự):", IdentityBackup.MinPasswordLength), L.T("Tiếp"), confirm: true,
                p => Task.FromResult(p.Length < IdentityBackup.MinPasswordLength ? L.F("Mật khẩu phải có ít nhất {0} ký tự.", IdentityBackup.MinPasswordLength) : null))
            is not { } password)
        {
            return;
        }

        string name = $"Shorekeeper-{device.DeviceId.ShortForm}-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{IdentityBackup.FileExtension}";
        if (await dialogs.PickSaveFileAsync(title, name, BackupFileType) is not { } path)
        {
            return;
        }

        try
        {
            await Task.Run(() => backup.ExportAsync(path, password, device.DisplayName));
        }
        catch (IOException ex)
        {
            await dialogs.ChooseAsync(title, L.F("Không ghi được file: {0}", ex.Message), [L.T("Đóng")]);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            await dialogs.ChooseAsync(title, L.T("Không có quyền ghi vào thư mục đã chọn."), [L.T("Đóng")]);
            return;
        }

        await dialogs.ChooseAsync(
            L.T("Đã sao lưu"),
            L.T("Cất file và mật khẩu ở nơi an toàn: ai có cả hai có thể giả làm máy này.\n\nKhi cài lại Windows hoặc chuyển sang máy mới, vào Cài đặt → Khôi phục để giữ nguyên danh tính, liên hệ và nhóm."),
            [L.T("Đóng")]);
    }

    /// <summary>
    /// "Khôi phục…": the file, then its password (a wrong one is said in the same dialog), then what restoring would do.
    /// The backup replaces the identity, contacts and groups at the next start.
    /// </summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        string title = L.T("Khôi phục danh tính");
        if (await dialogs.PickFileAsync(title, BackupFileType) is not { } path)
        {
            return;
        }

        OpenedBackup? opened = null;
        if (await dialogs.PromptPasswordAsync(title, L.T("Mật khẩu của bản sao lưu:"), L.T("Mở"), confirm: false, async password =>
            {
                try
                {
                    opened = await Task.Run(async () => IdentityBackup.Open(await File.ReadAllBytesAsync(path), password));
                    return null;
                }
                catch (CryptographicException)
                {
                    return L.T("Sai mật khẩu, hoặc file đã bị thay đổi.");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return L.T("Không đọc được: file này không phải bản sao lưu Shorekeeper, hoặc đã hỏng.");
                }
            }) is null || opened is null)
        {
            return;
        }

        BackupSummary summary = opened.Summary;
        int choice = await dialogs.ChooseAsync(
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
        IReadOnlyList<string> picked = await dialogs.PickFolderAsync();
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
            AutoAcceptLimitGb = AutoAcceptLimitGb is > 0 and var gb ? (double)gb : current.AutoAcceptLimitGb,
            QuickSendHotkey = QuickSendHotkey,
            QuickSendShortcut = QuickSendShortcut,
            Language = Language.Code,
        });
        Load(saved);
        StatusText = saved.OfferLifetime < TimeSpan.FromHours(OfferLifetime.Value)
            ? L.T("Đã lưu. Quản trị viên giới hạn thời hạn mở offer ngắn hơn lựa chọn của bạn.")
            : L.T("Đã lưu.");

        // Text is translated when it is created, so a new language needs a fresh start.
        if (languageChanged
            && await dialogs.ChooseAsync(L.T("Ngôn ngữ"), L.T("Ngôn ngữ mới có hiệu lực sau khi khởi động lại Shorekeeper."), [L.T("Khởi động lại"), L.T("Để sau")]) == 0)
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
        AutoAcceptLimitGb = (decimal)current.AutoAcceptLimitGb;
        QuickSendHotkey = current.QuickSendHotkey;
        QuickSendShortcut = current.QuickSendShortcut.ToString();
        OnPropertyChanged(nameof(HotkeyWarning));
        Language = LanguageChoice.Of(settings.User.Language);
        Theme = ThemeChoice.Of(settings.User.Theme);
        OfferLifetime = Closest(OfferLifetimeChoices, settings.User.OfferLifetimeHours ?? (int)current.OfferLifetime.TotalHours);
        ReconnectWindow = Closest(ReconnectChoices, (int)current.ReconnectWindow.TotalSeconds);
        IsDownloadDirectoryLocked = current.IsLocked(nameof(ShorekeeperSettings.DownloadDirectory));
        IsReconnectWindowLocked = current.IsLocked(nameof(ShorekeeperSettings.ReconnectWindowSeconds));
    }

    partial void OnIsRecordingShortcutChanged(bool value) => quickSend.PauseHotkey(value);

    private static SettingChoice Closest(IReadOnlyList<SettingChoice> choices, int value) =>
        choices.MinBy(c => Math.Abs(c.Value - value))!;
}
