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
using Shorekeeper.Core.Settings;
using Shorekeeper.Desktop.Localization;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.ViewModels;

public enum BackupStep
{
    None,
    Backup,
    RestorePassword,
    RestoreConfirm,
}

/// <summary>
/// "Cài đặt" on the phone (docs/14-android.md, A5): name, "Luôn nhận file", identity backup and restore (to move to
/// a new phone without connecting again, ADR-013), and what the network looks like from here.
/// </summary>
public sealed partial class SettingsTabViewModel : ObservableObject
{
    private readonly SettingsService settings;
    private readonly IdentityBackup backup;
    private readonly IExternalFileSource external;
    private readonly PeerDirectory directory;
    private readonly ApiServer api;
    private readonly LocalDevice device;
    private readonly AppPaths paths;
    private readonly MainViewModel main;
    private readonly string? startLanguage;
    private OpenedBackup? opened;
    private string? restoreLocation;

    public SettingsTabViewModel(IServiceProvider services, MainViewModel main)
    {
        settings = services.GetRequiredService<SettingsService>();
        backup = services.GetRequiredService<IdentityBackup>();
        external = services.GetRequiredService<IExternalFileSource>();
        directory = services.GetRequiredService<PeerDirectory>();
        api = services.GetRequiredService<ApiServer>();
        device = services.GetRequiredService<LocalDevice>();
        paths = services.GetRequiredService<AppPaths>();
        Blocked = new BlockedPeersViewModel(services.GetRequiredService<TrustStore>());
        this.main = main;
        DisplayName = settings.Current.DisplayName;
        AutoAcceptLimitGb = (decimal)settings.Current.AutoAcceptLimitGb;
        startLanguage = settings.User.Language;
        Language = LanguageChoice.Of(startLanguage);
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(NetworkText)));
    }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    /// <summary>"Tự nhận file tối đa" (GB) for contacts that follow Settings.</summary>
    [ObservableProperty]
    public partial decimal? AutoAcceptLimitGb { get; set; }

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
            }
        }
    }

    /// <summary>"Đã chặn": the only place to undo a block.</summary>
    public BlockedPeersViewModel Blocked { get; }

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
    // Two buttons on the page; everything else happens in one dialog:
    // "Sao lưu…" → password twice; "Khôi phục…" → pick the file → its password → what restoring would do.

    /// <summary>The step the backup dialog shows; <see cref="BackupStep.None"/> when it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDialogOpen), nameof(IsBackupStep), nameof(IsRestorePasswordStep), nameof(IsRestoreConfirmStep), nameof(DialogOkText))]
    public partial BackupStep Step { get; set; }

    public bool IsDialogOpen => Step != BackupStep.None;

    public bool IsBackupStep => Step == BackupStep.Backup;

    public bool IsRestorePasswordStep => Step == BackupStep.RestorePassword;

    public bool IsRestoreConfirmStep => Step == BackupStep.RestoreConfirm;

    public string DialogOkText => Step switch
    {
        BackupStep.Backup => L.T("Sao lưu"),
        BackupStep.RestorePassword => L.T("Mở"),
        _ => L.T("Khôi phục và khởi động lại"),
    };

    public string BackupPrompt { get; } = L.F("Đặt mật khẩu cho bản sao lưu (ít nhất {0} ký tự):", IdentityBackup.MinPasswordLength);

    [ObservableProperty]
    public partial string BackupPassword { get; set; } = "";

    [ObservableProperty]
    public partial string BackupPasswordAgain { get; set; } = "";

    [ObservableProperty]
    public partial string RestorePassword { get; set; } = "";

    /// <summary>A problem to fix without leaving the dialog (short password, wrong password…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialogError))]
    public partial string? DialogError { get; set; }

    public bool HasDialogError => DialogError is not null;

    /// <summary>The outcome, under the buttons once the dialog has closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupStatus))]
    public partial string? BackupStatus { get; set; }

    public bool HasBackupStatus => BackupStatus is not null;

    /// <summary>What restoring the opened backup would do.</summary>
    [ObservableProperty]
    public partial string? RestoreSummary { get; set; }

    // ───────────── Network ─────────────

    public string NetworkText
    {
        get
        {
            IReadOnlyList<LocalInterface> interfaces = LocalInterfaces.GetUsable();
            int heard = directory.Snapshot().Count(p => p.Source == PeerSource.Direct);
            string addresses = interfaces.Count == 0 ? L.T("không có (chưa vào Wi-Fi?)") : string.Join(", ", interfaces.Select(i => i.Address));
            string hint = heard == 0
                ? L.T("\nChưa thấy máy nào. Nếu máy tính đã mở Shorekeeper cùng Wi-Fi mà vẫn không thấy, Wi-Fi có thể đang cách ly các thiết bị (mạng khách, mạng công ty): thử thêm máy bằng địa chỉ (nút + ở tab Xóm), hoặc dùng điểm phát Wi-Fi của điện thoại.")
                : "";
            return L.F("Địa chỉ của điện thoại: {0}\nCổng HTTPS: {1}\nĐang thấy trực tiếp: {2} máy{3}", addresses, api.Port, heard, hint);
        }
    }

    [RelayCommand]
    private void SaveAutoAcceptLimit()
    {
        if (AutoAcceptLimitGb is not ({ } gb and > 0) || gb > (decimal)EffectiveSettings.MaxAutoAcceptLimitGb)
        {
            main.Notice = L.F("Nhập số GB lớn hơn 0 và không quá {0}.", EffectiveSettings.MaxAutoAcceptLimitGb.ToString("N0", L.Culture));
            return;
        }

        AutoAcceptLimitGb = (decimal)settings.Update(s => s with { AutoAcceptLimitGb = (double)gb }).AutoAcceptLimitGb;
        main.Notice = L.T("Đã lưu.");
    }

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

    [RelayCommand]
    private void StartBackup()
    {
        BackupPassword = BackupPasswordAgain = "";
        DialogError = BackupStatus = null;
        Step = BackupStep.Backup;
    }

    /// <summary>"Khôi phục…": the view picked a file (a content:// URI or a path); its password comes next.</summary>
    internal void StartRestore(string location)
    {
        restoreLocation = location;
        opened = null;
        RestorePassword = "";
        DialogError = BackupStatus = null;
        Step = BackupStep.RestorePassword;
    }

    [RelayCommand]
    private Task DialogOkAsync() => Step switch
    {
        BackupStep.Backup => BackupAsync(),
        BackupStep.RestorePassword => OpenBackupAsync(),
        BackupStep.RestoreConfirm => ConfirmRestore(),
        _ => Task.CompletedTask,
    };

    [RelayCommand]
    private void CancelDialog()
    {
        opened = null;
        restoreLocation = null;
        BackupPassword = BackupPasswordAgain = RestorePassword = "";
        DialogError = null;
        Step = BackupStep.None;
    }

    /// <summary>Writes the .skbackup into the download folder, where the Files app finds it.</summary>
    private async Task BackupAsync()
    {
        if (BackupPassword.Length < IdentityBackup.MinPasswordLength)
        {
            DialogError = L.F("Mật khẩu phải có ít nhất {0} ký tự.", IdentityBackup.MinPasswordLength);
            return;
        }

        if (BackupPassword != BackupPasswordAgain)
        {
            DialogError = L.T("Hai lần nhập mật khẩu không khớp.");
            return;
        }

        string path = Path.Combine(paths.DefaultDownloadDirectory,
            $"Shorekeeper-{device.DeviceId.ShortForm}-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{IdentityBackup.FileExtension}");
        try
        {
            Directory.CreateDirectory(paths.DefaultDownloadDirectory);
            string password = BackupPassword;
            await Task.Run(() => backup.ExportAsync(path, password, device.DisplayName));
            CancelDialog();
            BackupStatus = L.F("Đã sao lưu vào {0}. Cất file và mật khẩu ở nơi an toàn: ai có cả hai có thể giả làm máy này.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DialogError = L.F("Không ghi được file: {0}", ex.Message);
        }
    }

    private async Task OpenBackupAsync()
    {
        if (restoreLocation is not { } location)
        {
            return;
        }

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
            DialogError = L.T("Sai mật khẩu, hoặc file đã bị thay đổi.");
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            DialogError = L.T("Không đọc được: file này không phải bản sao lưu Shorekeeper, hoặc đã hỏng.");
            return;
        }

        BackupSummary summary = opened.Summary;
        DialogError = null;
        Step = BackupStep.RestoreConfirm;
        RestoreSummary = L.F(
            "Danh tính {0} ({1}, sao lưu lúc {2:HH:mm dd/MM/yyyy}) với {3} liên hệ và {4} nhóm. Danh tính hiện tại ({5}) cùng liên hệ và nhóm sẽ bị thay; app sẽ khởi động lại. Đừng chạy cùng một danh tính trên hai máy cùng lúc.",
            summary.DeviceId.ShortForm, summary.DisplayName, summary.CreatedAt.ToLocalTime(), summary.Contacts, summary.Groups, device.DeviceId.ShortForm);
    }

    private Task ConfirmRestore()
    {
        if (opened is not null)
        {
            backup.StageRestore(opened);
            AppRestart.Restart(global::Android.App.Application.Context);
        }

        return Task.CompletedTask;
    }
}
