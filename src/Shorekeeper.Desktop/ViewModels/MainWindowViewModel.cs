using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Desktop.ViewModels;

public enum NavPage
{
    Neighborhood,
    Messages,
    Inbox,
    Sent,
    Groups,
    Audio,
    Settings,
}

/// <param name="milestone">Set while the page is not built yet.</param>
/// <param name="icon">Key of a geometry in Themes/Icons.axaml.</param>
public sealed partial class NavItem(NavPage page, string title, string icon, string? milestone = null) : ObservableObject
{
    public NavPage Page { get; } = page;

    public string Title { get; } = title;

    public Geometry? Icon { get; } = Application.Current?.FindResource(icon) as Geometry;

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
        GroupsViewModel groups,
        AudioViewModel audio,
        StatsViewModel stats,
        ChatViewModel chat,
        DialogService dialogs)
    {
        Groups = groups;
        Chat = chat;
        Stats = stats;
        Audio = audio;
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
        ClipboardDirectory = paths.ClipboardDirectory;
        AppVersion = typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "0.0.0";
        SelectedNavItem = NavItems[0];
        SelectedStatus = StatusOptions.First(o => o.Status == presence.Status);

        Nav(NavPage.Inbox).Badge = inbox.NewCount;
        inbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InboxViewModel.NewCount))
            {
                Nav(NavPage.Inbox).Badge = inbox.NewCount;
            }
        };
        Nav(NavPage.Groups).Badge = groups.AttentionCount;
        groups.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GroupsViewModel.AttentionCount))
            {
                Nav(NavPage.Groups).Badge = groups.AttentionCount;
            }
        };
        Nav(NavPage.Messages).Badge = chat.UnreadTotal;
        chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.UnreadTotal))
            {
                Nav(NavPage.Messages).Badge = chat.UnreadTotal;
            }
        };
        neighborhood.ChatRequested += (_, peer) => OpenChat(peer);
        groups.ChatRequested += (_, groupId) => OpenGroupChat(groupId);
        chat.ShowsGroups = true;
        chat.CopyText = text => _ = DialogService.CopyTextAsync(text);
        chat.OpenLink = DialogService.OpenLink;
        chat.Confirm = async (title, question, action) => await DialogService.ChooseAsync(title, question, [action, L.T("Hủy")]) == 0;
        chat.ShowFiles = received => ShowPage(received ? NavPage.Inbox : NavPage.Sent);
        chat.PickFiles = () => DialogService.PickFilesAsync();
        chat.OpenFile = path =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Win32Exception)
            {
                // No viewer for it, or blocked by policy: the card stays as it is.
            }
        };
        chat.SendFiles = async (recipients, groupId, paths) =>
        {
            // Problems are asked or told by the dialogs themselves.
            await dialogs.SendAsync(paths, [new OfferBatch(recipients, groupId)], showSent: false);
            return null;
        };
        // Downloads fill the disk without telling the window; once a minute is enough for the card.
        RefreshStorage();
        DispatcherTimer.Run(() =>
        {
            RefreshStorage();
            return true;
        }, TimeSpan.FromMinutes(1));
        Search = new SearchViewModel(neighborhood, groups, inbox, sent, Reveal);
        dialogs.OfferSent += (_, _) => SelectedNavItem = Nav(NavPage.Sent);
        dialogs.PageRequested += (_, page) => SelectedNavItem = Nav(page);
        audio.Notice += (_, message) => dialogs.Ask(L.T("Âm thanh"), [message], [], NavPage.Audio, _ => { });
        audio.AskPassword = question => question.Masked
            ? DialogService.PromptPasswordAsync(question.Title, question.Label, question.OkText, confirm: false, _ => Task.FromResult<string?>(null))
            : DialogService.PromptAsync(question.Title, question.Label, "", question.OkText);
    }

    public NeighborhoodViewModel Neighborhood { get; }

    public InboxViewModel Inbox { get; }

    public SentViewModel Sent { get; }

    public NetworkDiagnosticsViewModel Diagnostics { get; }

    public BlockedPeersViewModel Blocked { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>"Thống kê" in Settings.</summary>
    public StatsViewModel Stats { get; }

    public GroupsViewModel Groups { get; }

    public AudioViewModel Audio { get; }

    public SearchViewModel Search { get; }

    /// <summary>"Tin nhắn".</summary>
    public ChatViewModel Chat { get; }

    /// <summary>A search result was chosen: the view scrolls to this row's view model and points at it.</summary>
    public event EventHandler<object>? RevealRequested;

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    public string DeviceId { get; }

    public string DeviceIdShort { get; }

    [ObservableProperty]
    public partial string DownloadDirectory { get; set; }

    public string DataDirectory { get; }

    /// <summary>Where a picture pasted with Ctrl+V is saved before it is sent.</summary>
    public string ClipboardDirectory { get; }

    public string AppVersion { get; }

    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new(NavPage.Neighborhood, L.T("Xóm"), "Icon.People"),
        new(NavPage.Messages, L.T("Tin nhắn"), "Icon.Chat"),
        new(NavPage.Inbox, L.T("Hộp nhận"), "Icon.Download"),
        new(NavPage.Sent, L.T("Đã gửi"), "Icon.Upload"),
        new(NavPage.Groups, L.T("Nhóm"), "Icon.Team"),
        new(NavPage.Audio, L.T("Âm thanh"), "Icon.Speaker"),
        new(NavPage.Settings, L.T("Cài đặt"), "Icon.Settings"),
    ];

    public IReadOnlyList<StatusOption> StatusOptions { get; } =
    [
        new(MyStatus.Online, "Online"),
        new(MyStatus.Busy, L.T("Bận")),
        new(MyStatus.Hidden, L.T("Ẩn")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComingSoonText), nameof(IsComingSoonVisible), nameof(IsNeighborhoodVisible), nameof(IsMessagesVisible),
        nameof(IsPageScrollerVisible), nameof(IsInboxVisible), nameof(IsSentVisible), nameof(IsGroupsVisible), nameof(IsAudioVisible), nameof(IsSettingsVisible))]
    public partial NavItem SelectedNavItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsHidden))]
    public partial StatusOption SelectedStatus { get; set; }

    public bool IsBusy => SelectedStatus.Status == MyStatus.Busy;

    public bool IsHidden => SelectedStatus.Status == MyStatus.Hidden;

    public bool IsNeighborhoodVisible => SelectedNavItem.Page == NavPage.Neighborhood;

    /// <summary>"Tin nhắn" lays out its own scrolling: the list, the messages, and the box pinned at the bottom.</summary>
    public bool IsMessagesVisible => SelectedNavItem.Page == NavPage.Messages;

    public bool IsPageScrollerVisible => !IsMessagesVisible;

    public bool IsInboxVisible => SelectedNavItem.Page == NavPage.Inbox;

    public bool IsSentVisible => SelectedNavItem.Page == NavPage.Sent;

    public bool IsGroupsVisible => SelectedNavItem.Page == NavPage.Groups;

    public bool IsAudioVisible => SelectedNavItem.Page == NavPage.Audio;

    public bool IsSettingsVisible => SelectedNavItem.Page == NavPage.Settings;

    public bool IsComingSoonVisible => SelectedNavItem.Milestone is not null;

    public string ComingSoonText => L.F("Mục \"{0}\" sẽ có ở {1}.", SelectedNavItem.Title, SelectedNavItem.Milestone);

    /// <summary>The main window is in front: with "Tin nhắn" shown, what arrives there is read.</summary>
    [ObservableProperty]
    public partial bool IsWindowActive { get; set; }

    partial void OnIsWindowActiveChanged(bool value) => Chat.IsShown = value && IsMessagesVisible;

    /// <summary>"Nhắn tin" on a card, or a message's toast.</summary>
    public void OpenChat(DeviceId peer)
    {
        Chat.Open(peer);
        ShowPage(NavPage.Messages);
    }

    /// <summary>"Nhắn tin" on a group, or a group message's toast.</summary>
    public void OpenGroupChat(string groupId)
    {
        Chat.OpenGroup(groupId);
        ShowPage(NavPage.Messages);
    }

    partial void OnSelectedNavItemChanged(NavItem value)
    {
        Chat.IsShown = IsWindowActive && value.Page == NavPage.Messages;
        if (value.Page == NavPage.Settings)
        {
            Diagnostics.RefreshCommand.Execute(null);
            Stats.RefreshCommand.Execute(null);
        }
        else if (value.Page == NavPage.Audio)
        {
            Audio.RefreshOutputDevices();
        }
        else if (value.Page == NavPage.Inbox)
        {
            // Received files may have been deleted or moved since ("Đã xóa file").
            Inbox.CheckFiles();
        }
    }

    /// <summary>Used share of the drive holding the download folder, 0 to 1 ("this device" card).</summary>
    [ObservableProperty]
    public partial double StorageUsed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStorage))]
    public partial string? StorageText { get; set; }

    public bool HasStorage => StorageText is not null;

    [RelayCommand]
    private void ShowPage(NavPage page) => SelectedNavItem = Nav(page);

    private NavItem Nav(NavPage page) => NavItems.First(i => i.Page == page);

    private void Reveal(SearchResult result)
    {
        if (result.Item is PeerCardViewModel person)
        {
            // Selecting lets Ctrl+V send to them right away.
            Neighborhood.Select(person, toggle: false);
        }

        ShowPage(result.Kind switch
        {
            SearchKind.Person => NavPage.Neighborhood,
            SearchKind.Group => NavPage.Groups,
            SearchKind.Received => NavPage.Inbox,
            _ => NavPage.Sent,
        });
        RevealRequested?.Invoke(this, result.Item);
    }

    partial void OnSelectedStatusChanged(StatusOption value) => presence.Status = value.Status;

    partial void OnDownloadDirectoryChanged(string value) => RefreshStorage();

    private void RefreshStorage()
    {
        try
        {
            DriveInfo drive = DiskSpace.DriveOf(DownloadDirectory);
            StorageUsed = drive.TotalSize == 0 ? 0 : 1 - ((double)drive.AvailableFreeSpace / drive.TotalSize);
            StorageText = L.F("Trống {0} / {1}", TransferText.Size(drive.AvailableFreeSpace), TransferText.Size(drive.TotalSize));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unplugged drive or unreadable folder: hide the bar rather than show a wrong number.
            StorageText = null;
        }
    }
}
