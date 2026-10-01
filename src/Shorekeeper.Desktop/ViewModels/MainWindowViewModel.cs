using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Desktop.ViewModels;

public enum NavPage
{
    Neighborhood,
    Inbox,
    Sent,
    Groups,
    Settings,
}

/// <param name="Milestone">Set while the page is not built yet.</param>
public sealed record NavItem(NavPage Page, string Title, string? Milestone = null);

public sealed record StatusOption(MyStatus Status, string Label);

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LocalPresence presence;

    public MainWindowViewModel(
        ShorekeeperEngine engine,
        SettingsService settings,
        AppPaths paths,
        LocalPresence presence,
        NeighborhoodViewModel neighborhood,
        NetworkDiagnosticsViewModel diagnostics)
    {
        this.presence = presence;
        Neighborhood = neighborhood;
        Diagnostics = diagnostics;
        DeviceId = engine.Identity.DeviceId.Value;
        DeviceIdShort = engine.Identity.DeviceId.ShortForm;
        DisplayName = settings.Current.DisplayName;
        DownloadDirectory = settings.Current.DownloadDirectory;
        DataDirectory = paths.DataDirectory;
        AppVersion = typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "0.0.0";
        SelectedNavItem = NavItems[0];
        SelectedStatus = StatusOptions.First(o => o.Status == presence.Status);
    }

    public NeighborhoodViewModel Neighborhood { get; }

    public NetworkDiagnosticsViewModel Diagnostics { get; }

    public string DisplayName { get; }

    public string DeviceId { get; }

    public string DeviceIdShort { get; }

    public string DownloadDirectory { get; }

    public string DataDirectory { get; }

    public string AppVersion { get; }

    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new(NavPage.Neighborhood, "Xóm"),
        new(NavPage.Inbox, "Hộp nhận", "M3"),
        new(NavPage.Sent, "Đã gửi", "M3"),
        new(NavPage.Groups, "Nhóm", "M5"),
        new(NavPage.Settings, "Cài đặt"),
    ];

    public IReadOnlyList<StatusOption> StatusOptions { get; } =
    [
        new(MyStatus.Online, "Online"),
        new(MyStatus.Busy, "Bận"),
        new(MyStatus.Hidden, "Ẩn"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComingSoonText), nameof(IsComingSoonVisible), nameof(IsNeighborhoodVisible), nameof(IsSettingsVisible))]
    public partial NavItem SelectedNavItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsHidden))]
    public partial StatusOption SelectedStatus { get; set; }

    public bool IsBusy => SelectedStatus.Status == MyStatus.Busy;

    public bool IsHidden => SelectedStatus.Status == MyStatus.Hidden;

    public bool IsNeighborhoodVisible => SelectedNavItem.Page == NavPage.Neighborhood;

    public bool IsSettingsVisible => SelectedNavItem.Page == NavPage.Settings;

    public bool IsComingSoonVisible => SelectedNavItem.Milestone is not null;

    public string ComingSoonText => $"Mục \"{SelectedNavItem.Title}\" sẽ có ở {SelectedNavItem.Milestone}.";

    partial void OnSelectedNavItemChanged(NavItem value)
    {
        if (value.Page == NavPage.Settings)
        {
            Diagnostics.RefreshCommand.Execute(null);
        }
    }

    partial void OnSelectedStatusChanged(StatusOption value) => presence.Status = value.Status;
}
