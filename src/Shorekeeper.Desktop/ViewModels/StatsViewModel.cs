using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Monitoring;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>A row of "Thống kê": a period, then what was sent and received in it.</summary>
public sealed record StatsPeriod(string Label, string Sent, string Received);

/// <summary>A device in "Theo máy".</summary>
public sealed record StatsPeer(string Name, string Text);

/// <summary>A kind of received file in the download folder, with its share of the folder (0 to 100).</summary>
public sealed record StatsKind(string Name, string Text, double Share);

/// <summary>
/// "Thống kê" in Settings (docs/10-ux.md §15), on the desktop and the phone: counts from <see cref="UsageStatsService"/>,
/// and the drive and the download folder, read again each time Settings is shown.
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly UsageStatsService usage;
    private readonly TrustStore trust;
    private readonly SettingsService settings;

    public StatsViewModel(UsageStatsService usage, TrustStore trust, SettingsService settings)
    {
        this.usage = usage;
        this.trust = trust;
        this.settings = settings;
        usage.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshCounts);
        RefreshCounts();
    }

    [ObservableProperty]
    public partial IReadOnlyList<StatsPeriod> Periods { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPeers))]
    public partial IReadOnlyList<StatsPeer> Peers { get; set; } = [];

    public bool HasPeers => Peers.Count > 0;

    /// <summary>"7 ngày: nghe 1 giờ 5 phút · phát 20 phút".</summary>
    [ObservableProperty]
    public partial string SoundWeek { get; set; } = "";

    [ObservableProperty]
    public partial string SoundTotal { get; set; } = "";

    /// <summary>"Đếm từ 20/10/2026 · chỉ lưu trên máy này".</summary>
    [ObservableProperty]
    public partial string Since { get; set; } = "";

    [ObservableProperty]
    public partial bool IsMeasuring { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrive))]
    public partial string? DriveText { get; set; }

    public bool HasDrive => DriveText is not null;

    /// <summary>Used share of the drive, 0 to 100.</summary>
    [ObservableProperty]
    public partial double DriveUsed { get; set; }

    [ObservableProperty]
    public partial string FolderText { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<StatsKind> Kinds { get; set; } = [];

    /// <summary>Settings shown or [Làm mới]: counts now, the disk in the background.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        RefreshCounts();
        if (IsMeasuring)
        {
            return;
        }

        IsMeasuring = true;
        string folder = settings.Current.DownloadDirectory;
        StorageReport report = await Task.Run(() => StorageReport.Measure(folder));
        IsMeasuring = false;

        DriveText = report.DriveTotal > 0
            ? L.F("Còn trống {0} / {1}", TransferText.Size(report.DriveFree), TransferText.Size(report.DriveTotal))
            : null;
        DriveUsed = report.DriveTotal > 0 ? 100.0 * (report.DriveTotal - report.DriveFree) / report.DriveTotal : 0;
        FolderText = L.F("Thư mục nhận chiếm {0} · {1}", TransferText.Size(report.FolderBytes), TransferText.FileCount(report.FolderFiles));
        Kinds = [.. report.Kinds.Select(k => new StatsKind(
            NameOf(k.Kind),
            $"{TransferText.Size(k.Bytes)} · {TransferText.FileCount(k.Files)}",
            report.FolderBytes == 0 ? 0 : 100.0 * k.Bytes / report.FolderBytes))];
    }

    private void RefreshCounts()
    {
        UsageSummary summary = usage.Summary();
        Periods =
        [
            Period(L.T("Hôm nay"), summary.Today),
            Period(L.T("7 ngày"), summary.Week),
            Period(L.T("30 ngày"), summary.Month),
            Period(L.T("Tổng"), summary.Total),
        ];
        Peers = [.. summary.Peers.Select(p => new StatsPeer(
            trust.Get(p.PeerId)?.ShownName ?? p.PeerId.ShortForm,
            L.F("Gửi {0} · nhận {1}", Amount(p.SentFiles, p.SentBytes), Amount(p.ReceivedFiles, p.ReceivedBytes))))];
        SoundWeek = L.F("7 ngày: nghe {0} · phát {1}", Duration(summary.Week.ListenSeconds), Duration(summary.Week.PlaySeconds));
        SoundTotal = L.F("Tổng: nghe {0} · phát {1}", Duration(summary.Total.ListenSeconds), Duration(summary.Total.PlaySeconds));
        Since = L.F("Đếm từ {0:d} · chỉ lưu trên máy này", summary.Since.ToLocalTime());
    }

    private static StatsPeriod Period(string label, UsageCounts counts) =>
        new(label, Amount(counts.SentFiles, counts.SentBytes), Amount(counts.ReceivedFiles, counts.ReceivedBytes));

    /// <summary>"3 file · 12,0 MB".</summary>
    private static string Amount(long files, long bytes) => $"{TransferText.FileCount((int)files)} · {TransferText.Size(bytes)}";

    /// <summary>"1 giờ 5 phút", "20 phút".</summary>
    private static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? L.F("{0} giờ {1} phút", (int)span.TotalHours, span.Minutes) : L.F("{0} phút", span.Minutes);
    }

    private static string NameOf(StorageKind kind) => kind switch
    {
        StorageKind.Pictures => L.T("Ảnh"),
        StorageKind.Videos => L.T("Video"),
        StorageKind.Audio => L.T("Âm thanh"),
        StorageKind.Documents => L.T("Tài liệu"),
        StorageKind.Archives => L.T("File nén, cài đặt"),
        _ => L.T("Khác"),
    };
}
