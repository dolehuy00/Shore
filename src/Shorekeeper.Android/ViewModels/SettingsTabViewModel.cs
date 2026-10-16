using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Android.Platform;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Desktop.Localization;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Android.ViewModels;

/// <summary>
/// "Cài đặt" on the phone (docs/14-android.md, A5): name, "Luôn nhận file", identity backup and restore (to move to
/// a new phone without connecting again, ADR-013), and what the network looks like from here, with "add by address"
/// for Wi-Fi that hides devices from each other (R7).
/// </summary>
public sealed partial class SettingsTabViewModel : ObservableObject
{
    private readonly SettingsService settings;
    private readonly IdentityBackup backup;
    private readonly IExternalFileSource external;
    private readonly ManualPeerFinder finder;
    private readonly PeerDirectory directory;
    private readonly ApiServer api;
    private readonly LocalDevice device;
    private readonly AppPaths paths;
    private readonly MainViewModel main;
    private readonly string? startLanguage;
    private OpenedBackup? opened;

    public SettingsTabViewModel(IServiceProvider services, MainViewModel main)
    {
        settings = services.GetRequiredService<SettingsService>();
        backup = services.GetRequiredService<IdentityBackup>();
        external = services.GetRequiredService<IExternalFileSource>();
        finder = services.GetRequiredService<ManualPeerFinder>();
        directory = services.GetRequiredService<PeerDirectory>();
        api = services.GetRequiredService<ApiServer>();
        device = services.GetRequiredService<LocalDevice>();
        paths = services.GetRequiredService<AppPaths>();
        this.main = main;
        DisplayName = settings.Current.DisplayName;
        startLanguage = settings.User.Language;
        Language = LanguageChoice.Of(startLanguage);
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(NetworkText)));
    }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    /// <summary>"Luôn nhận file": keeps a foreground service so PCs reach the phone with the screen off.</summary>
    public bool AlwaysReceive
    {
        get => BackgroundTransfers.AlwaysReceive;
        set
        {
            BackgroundTransfers.AlwaysReceive = value;
            OnPropertyChanged();
            if (value)
            {
                BatteryOptimization.RequestExemption(global::Android.App.Application.Context);
                main.Notice = L.T("Máy OPPO, Xiaomi, Vivo… còn có trình tiết kiệm pin riêng: vào Cài đặt → Pin → Shorekeeper và cho phép hoạt động nền, nếu không hệ thống vẫn có thể tắt app.");
            }
        }
    }

    // ───────────── Language ─────────────

    /// <summary>Saved as soon as it is picked; text is translated when it is created, so it shows after a restart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    public partial LanguageChoice Language { get; set; }

    public bool NeedsRestart => Language.Code != startLanguage;

    partial void OnLanguageChanged(LanguageChoice value)
    {
        if (value.Code != settings.User.Language)
        {
            settings.Update(s => s with { Language = value.Code });
        }
    }

    [RelayCommand]
    private void RestartApp()
    {
        if (NeedsRestart)
        {
            AppRestart.Restart(global::Android.App.Application.Context);
        }
    }

    // ───────────── Backup ─────────────

    [ObservableProperty]
    public partial string BackupPassword { get; set; } = "";

    [ObservableProperty]
    public partial string BackupPasswordAgain { get; set; } = "";

    [ObservableProperty]
    public partial string RestorePassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupStatus))]
    public partial string? BackupStatus { get; set; }

    public bool HasBackupStatus => BackupStatus is not null;

    /// <summary>Set once a backup is opened: what restoring it would do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmRestore))]
    public partial string? RestoreSummary { get; set; }

    public bool CanConfirmRestore => RestoreSummary is not null;

    // ───────────── Network ─────────────

    public string NetworkText
    {
        get
        {
            IReadOnlyList<LocalInterface> interfaces = LocalInterfaces.GetUsable();
            int heard = directory.Snapshot().Count(p => p.Source == PeerSource.Direct);
            string addresses = interfaces.Count == 0 ? L.T("không có (chưa vào Wi-Fi?)") : string.Join(", ", interfaces.Select(i => i.Address));
            string hint = heard == 0
                ? L.T("\nChưa thấy máy nào. Nếu máy tính đã mở Shorekeeper cùng Wi-Fi mà vẫn không thấy, Wi-Fi có thể đang cách ly các thiết bị (mạng khách, mạng công ty): thử thêm máy bằng địa chỉ bên dưới, hoặc dùng điểm phát Wi-Fi của điện thoại.")
                : "";
            return L.F("Địa chỉ của điện thoại: {0}\nCổng HTTPS: {1}\nĐang thấy trực tiếp: {2} máy{3}", addresses, api.Port, heard, hint);
        }
    }

    [ObservableProperty]
    public partial string AddAddress { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddStatus))]
    public partial string? AddStatus { get; set; }

    public bool HasAddStatus => AddStatus is not null;

    [RelayCommand]
    private void SaveName()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            main.Notice = L.T("Tên hiển thị không được để trống.");
            return;
        }

        DisplayName = settings.Update(s => s with { DisplayName = DisplayName.Trim() }).DisplayName;
        main.Notice = L.T("Đã lưu tên.");
    }

    /// <summary>Writes the .skbackup into the download folder, where the Files app finds it.</summary>
    [RelayCommand]
    private async Task BackupAsync()
    {
        if (BackupPassword.Length < IdentityBackup.MinPasswordLength)
        {
            BackupStatus = L.F("Mật khẩu phải có ít nhất {0} ký tự.", IdentityBackup.MinPasswordLength);
            return;
        }

        if (BackupPassword != BackupPasswordAgain)
        {
            BackupStatus = L.T("Hai lần nhập mật khẩu không khớp.");
            return;
        }

        string path = Path.Combine(paths.DefaultDownloadDirectory,
            $"Shorekeeper-{device.DeviceId.ShortForm}-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{IdentityBackup.FileExtension}");
        try
        {
            Directory.CreateDirectory(paths.DefaultDownloadDirectory);
            string password = BackupPassword;
            await Task.Run(() => backup.ExportAsync(path, password, device.DisplayName));
            BackupStatus = L.F("Đã sao lưu vào {0}. Cất file và mật khẩu ở nơi an toàn: ai có cả hai có thể giả làm máy này.", path);
            BackupPassword = BackupPasswordAgain = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackupStatus = L.F("Không ghi được file: {0}", ex.Message);
        }
    }

    /// <summary>Called with the file picked by the view (a content:// URI or a path).</summary>
    internal async Task OpenBackupAsync(string location)
    {
        opened = null;
        RestoreSummary = null;
        try
        {
            string password = RestorePassword;
            opened = await Task.Run(async () =>
            {
                byte[] content;
                if (external.Owns(location))
                {
                    await using Stream stream = external.OpenRead(location);
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    content = buffer.ToArray();
                }
                else
                {
                    content = await File.ReadAllBytesAsync(location);
                }

                return IdentityBackup.Open(content, password);
            });
        }
        catch (CryptographicException)
        {
            BackupStatus = L.T("Sai mật khẩu, hoặc file đã bị thay đổi.");
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            BackupStatus = L.T("Không đọc được: file này không phải bản sao lưu Shorekeeper, hoặc đã hỏng.");
            return;
        }

        BackupSummary summary = opened.Summary;
        BackupStatus = null;
        RestoreSummary = L.F(
            "Danh tính {0} ({1}, sao lưu lúc {2:HH:mm dd/MM/yyyy}) với {3} liên hệ và {4} nhóm. Danh tính hiện tại ({5}) cùng liên hệ và nhóm sẽ bị thay; app sẽ khởi động lại. Đừng chạy cùng một danh tính trên hai máy cùng lúc.",
            summary.DeviceId.ShortForm, summary.DisplayName, summary.CreatedAt.ToLocalTime(), summary.Contacts, summary.Groups, device.DeviceId.ShortForm);
    }

    [RelayCommand]
    private void ConfirmRestore()
    {
        if (opened is null)
        {
            return;
        }

        backup.StageRestore(opened);
        AppRestart.Restart(global::Android.App.Application.Context);
    }

    [RelayCommand]
    private void CancelRestore()
    {
        opened = null;
        RestoreSummary = null;
    }

    /// <summary>"Thêm máy": asks an address who it is, then connects; the address is kept so the phone finds it again.</summary>
    [RelayCommand]
    private async Task AddAsync(CancellationToken cancellationToken)
    {
        AddStatus = L.T("Đang tìm…");
        FindResult result = await finder.FindAsync(AddAddress, cancellationToken);
        if (result.Peer is not { } peer)
        {
            AddStatus = result.Error;
            return;
        }

        AddStatus = L.F("Tìm thấy {0} ({1}).", peer.Name, peer.Host);
        await finder.SaveAsync(AddAddress, cancellationToken);
        await main.ConnectAsync(peer.DeviceId, peer.Name);
    }
}
