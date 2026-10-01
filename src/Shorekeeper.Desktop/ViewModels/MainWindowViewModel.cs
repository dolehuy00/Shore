using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Desktop.Views;
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

/// <param name="milestone">Set while the page is not built yet.</param>
public sealed partial class NavItem(NavPage page, string title, string? milestone = null) : ObservableObject
{
    public NavPage Page { get; } = page;

    public string Title { get; } = title;

    public string? Milestone { get; } = milestone;

    /// <summary>E.g. the number of new offers in the inbox; 0 hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int Badge { get; set; }

    public bool HasBadge => Badge > 0;

    /// <summary>Used as the accessible name of the navigation item (screen readers).</summary>
    public override string ToString() => HasBadge ? $"{Title} ({Badge})" : Title;
}

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
        InboxViewModel inbox,
        SentViewModel sent,
        NetworkDiagnosticsViewModel diagnostics,
        BlockedPeersViewModel blocked,
        SettingsViewModel settingsPage,
        DialogService dialogs)
    {
        Settings = settingsPage;
        settings.Changed += (_, current) => Dispatcher.UIThread.Post(() =>
        {
            DisplayName = current.DisplayName;
            DownloadDirectory = current.DownloadDirectory;
        });
        this.presence = presence;
        Neighborhood = neighborhood;
        Inbox = inbox;
        Sent = sent;
        Diagnostics = diagnostics;
        Blocked = blocked;
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

        NavItems[1].Badge = inbox.NewCount;
        inbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InboxViewModel.NewCount))
            {
                NavItems[1].Badge = inbox.NewCount;
            }
        };
        dialogs.OfferSent += (_, _) => SelectedNavItem = NavItems[2];
        dialogs.InboxRequested += (_, _) => SelectedNavItem = NavItems[1];
    }

    public NeighborhoodViewModel Neighborhood { get; }

    public InboxViewModel Inbox { get; }

    public SentViewModel Sent { get; }

    public NetworkDiagnosticsViewModel Diagnostics { get; }

    public BlockedPeersViewModel Blocked { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    public string DeviceId { get; }

    public string DeviceIdShort { get; }

    [ObservableProperty]
    public partial string DownloadDirectory { get; set; }

    public string DataDirectory { get; }

    public string AppVersion { get; }

    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new(NavPage.Neighborhood, "Xóm"),
        new(NavPage.Inbox, "Hộp nhận"),
        new(NavPage.Sent, "Đã gửi"),
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
    [NotifyPropertyChangedFor(nameof(ComingSoonText), nameof(IsComingSoonVisible), nameof(IsNeighborhoodVisible),
        nameof(IsInboxVisible), nameof(IsSentVisible), nameof(IsSettingsVisible))]
    public partial NavItem SelectedNavItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsHidden))]
    public partial StatusOption SelectedStatus { get; set; }

    public bool IsBusy => SelectedStatus.Status == MyStatus.Busy;

    public bool IsHidden => SelectedStatus.Status == MyStatus.Hidden;

    public bool IsNeighborhoodVisible => SelectedNavItem.Page == NavPage.Neighborhood;

    public bool IsInboxVisible => SelectedNavItem.Page == NavPage.Inbox;

    public bool IsSentVisible => SelectedNavItem.Page == NavPage.Sent;

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
