using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Desktop.ViewModels;

public sealed record NavItem(string Title, string Milestone);

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(ShorekeeperEngine engine, SettingsService settings, AppPaths paths)
    {
        DeviceId = engine.Identity.DeviceId.Value;
        DeviceIdShort = engine.Identity.DeviceId.ShortForm;
        DisplayName = settings.Current.DisplayName;
        DownloadDirectory = settings.Current.DownloadDirectory;
        DataDirectory = paths.DataDirectory;
        AppVersion = typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "0.0.0";
        SelectedNavItem = NavItems[0];
    }

    public string DisplayName { get; }

    public string DeviceId { get; }

    public string DeviceIdShort { get; }

    public string DownloadDirectory { get; }

    public string DataDirectory { get; }

    public string AppVersion { get; }

    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new("Xóm", "M1"),
        new("Hộp nhận", "M3"),
        new("Đã gửi", "M3"),
        new("Nhóm", "M5"),
        new("Cài đặt", "M1"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComingSoonText))]
    public partial NavItem SelectedNavItem { get; set; }

    public string ComingSoonText => $"Mục \"{SelectedNavItem.Title}\" sẽ có ở {SelectedNavItem.Milestone}.";
}
