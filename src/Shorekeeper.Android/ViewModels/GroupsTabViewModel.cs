using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Android.Platform;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.ViewModels;

/// <summary>
/// "Nhóm" on the phone (docs/14-android.md, A5): the phone is a member, never a Host. Groups we are in (send to
/// members, leave), groups announced in the network ([Xin vào]), invitations, and what happened lately.
/// </summary>
public sealed partial class GroupsTabViewModel : ObservableObject
{
    private readonly GroupStore store;
    private readonly GroupService groups;
    private readonly PeerDirectory directory;
    private readonly LocalDevice device;
    private readonly TrustStore trust;
    private readonly MainViewModel main;
    private string shownGroups = "";

    public GroupsTabViewModel(
        GroupStore store, GroupService groups, PeerDirectory directory, LocalDevice device, TrustStore trust, MainViewModel main)
    {
        this.store = store;
        this.groups = groups;
        this.directory = directory;
        this.device = device;
        this.trust = trust;
        this.main = main;

        store.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        groups.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        // Names: a contact renamed itself, or we gave a member a name of our own.
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        groups.Notice += (_, notice) => Dispatcher.UIThread.Post(() => AddNotice(notice.Message));
        groups.InvitationReceived += (_, invitation) => Dispatcher.UIThread.Post(() =>
        {
            Refresh();
            main.Notice = L.F("{0} mời bạn vào nhóm {1}: xem tab Nhóm.", invitation.HostName, invitation.GroupName);
        });
        Refresh();
    }

    public bool IsEnabled => groups.IsEnabled;

    public ObservableCollection<GroupItemViewModel> MyGroups { get; } = [];

    public ObservableCollection<NetworkGroupItemViewModel> NetworkGroups { get; } = [];

    public ObservableCollection<InvitationItemViewModel> Invitations { get; } = [];

    public ObservableCollection<string> Notices { get; } = [];

    public bool HasMyGroups => MyGroups.Count > 0;

    public bool HasNetworkGroups => NetworkGroups.Count > 0;

    public bool HasInvitations => Invitations.Count > 0;

    public bool HasNotices => Notices.Count > 0;

    internal Task<string?> SendAsync(GroupItemViewModel group, IReadOnlyList<string> locations, string? text = null) =>
        store.Get(group.Id) is { Role: not GroupRole.Pending } current
            ? main.SendAsync(locations, [.. current.Members.Where(m => m.DeviceId != device.DeviceId).Select(m => (m.DeviceId, (string?)current.Id))], text)
            : Task.FromResult<string?>(L.T("Chưa ở trong nhóm này."));

    /// <summary>"Nhắn tin" on a group: its conversation in Tin nhắn.</summary>
    internal void OpenChat(GroupItemViewModel group) => main.OpenGroupChat(group.Id);

    /// <summary>"Gửi văn bản cho nhóm…": a link or a few lines to every other member.</summary>
    internal void AskText(GroupItemViewModel group) =>
        main.Question = new QuestionViewModel(L.T("Gửi văn bản"), L.F("Văn bản hoặc link gửi cho nhóm {0}:", group.Name), L.T("Gửi"),
            TextActions.Copied(global::Android.App.Application.Context),
            async text =>
            {
                if (!string.IsNullOrWhiteSpace(text) && await SendAsync(group, [], text) is { } error)
                {
                    main.Notice = error;
                }
            }, main);

    internal Task LeaveAsync(GroupItemViewModel group) => RunAsync(L.T("Không rời nhóm được"), () => groups.LeaveAsync(group.Id));

    internal Task JoinAsync(NetworkGroupItemViewModel group) =>
        RunAsync(L.T("Không gửi được yêu cầu"), () => groups.RequestJoinAsync(group.HostId, group.GroupId, group.Name, null));

    internal Task AcceptAsync(InvitationItemViewModel invitation) =>
        RunAsync(L.T("Không vào nhóm được"), () => groups.AcceptInvitationAsync(invitation.Invitation));

    internal void Decline(InvitationItemViewModel invitation) => groups.DismissInvitation(invitation.Invitation);

    private void Refresh()
    {
        IReadOnlyList<Group> all = store.GetAll();
        GroupItemViewModel[] mine = [.. all.Select(g => new GroupItemViewModel(g, Describe(g), [.. Members(g)], this))];
        // This runs whenever a device appears or goes quiet: the cards are only rebuilt when what they show changed.
        string shown = string.Join("\n", mine.Select(m => m.Signature));
        if (shown != shownGroups)
        {
            shownGroups = shown;
            Replace(MyGroups, mine);
        }

        HashSet<string> known = [.. all.Select(g => g.Id)];
        Replace(NetworkGroups, directory.Snapshot()
            .SelectMany(host => (host.HostedGroups ?? []).Where(g => !known.Contains(g.Id)).Select(g => (host, g)))
            .Select(x => new NetworkGroupItemViewModel(x.g, x.host.DeviceId, main.NameOf(x.host.DeviceId), this)));
        Replace(Invitations, groups.GetInvitations().Select(i => new InvitationItemViewModel(i, this)));

        OnPropertyChanged(nameof(HasMyGroups));
        OnPropertyChanged(nameof(HasNetworkGroups));
        OnPropertyChanged(nameof(HasInvitations));
        OnPropertyChanged(nameof(IsEnabled));
    }

    private string Describe(Group group)
    {
        if (group.Role == GroupRole.Pending)
        {
            return L.F("Host: {0} · đang chờ Host duyệt", main.NameOf(group.HostId));
        }

        int online = group.Members.Count(m => m.DeviceId == device.DeviceId || directory.Snapshot().Any(p => p.DeviceId == m.DeviceId && p.State == PeerState.Online));
        string host = group.Role == GroupRole.Host ? L.T("bạn") : main.NameOf(group.HostId);
        string unreachable = group.Role == GroupRole.Member && groups.IsHostReachable(group.Id) == false ? " · Host offline" : "";
        return L.F("Host: {0} · {1} thành viên · {2} online{3}", host, group.Members.Count, online, unreachable);
    }

    /// <summary>Host first, then you, then by name: on a phone or a computer, reachable or not, and where.</summary>
    private IEnumerable<GroupMemberItem> Members(Group group)
    {
        IReadOnlyList<PeerInfo> live = directory.Snapshot();
        return group.Members
            .Select(m =>
            {
                bool isMe = m.DeviceId == device.DeviceId;
                PeerInfo? peer = isMe ? null : live.FirstOrDefault(p => p.DeviceId == m.DeviceId);
                bool isOnline = isMe || peer?.State == PeerState.Online;
                return new GroupMemberItem(
                    isMe ? L.T("Bạn") : main.NameOf(m.DeviceId, m.Name),
                    m.IsHost,
                    isMe ? true : peer is not null ? peer.Os.Equals("android", StringComparison.OrdinalIgnoreCase) : trust.Get(m.DeviceId)?.IsPhone == true,
                    isOnline,
                    peer?.IsBusy == true,
                    isOnline ? peer?.IsBusy == true ? L.T("Bận") : "Online" : peer is null ? "Offline" : L.T("Mất tín hiệu…"),
                    isMe ? "" : peer is null ? "" : $"{peer.HostName} · {peer.Address}")
                {
                    IsMe = isMe,
                    RenameCommand = isMe ? null : new AsyncRelayCommand(() => main.Chat.RenameAsync(m.DeviceId, m.Name)),
                };
            })
            .OrderByDescending(m => m.IsHost)
            .ThenByDescending(m => m.IsMe)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private void AddNotice(string message)
    {
        Notices.Insert(0, $"{DateTime.Now:HH:mm} · {message}");
        while (Notices.Count > 5)
        {
            Notices.RemoveAt(Notices.Count - 1);
        }

        OnPropertyChanged(nameof(HasNotices));
    }

    /// <summary>Runs a group action; failures are explained in plain words, as on the desktop.</summary>
    private async Task RunAsync(string title, Func<Task> action)
    {
        try
        {
            await action();
            return;
        }
        catch (InvalidOperationException ex)
        {
            main.Notice = $"{title}: {ex.Message}";
        }
        catch (PeerUnreachableException)
        {
            main.Notice = L.F("{0}: không liên lạc được với Host.", title);
        }
        catch (PeerApiException ex)
        {
            main.Notice = $"{title}: " + ex.Status switch
            {
                404 => L.T("nhóm này không còn."),
                429 => L.T("đã gửi quá nhiều yêu cầu hoặc đã bị từ chối nhiều lần, thử lại sau."),
                503 => L.T("nhóm đã đủ thành viên."),
                _ => L.T("Host từ chối yêu cầu."),
            };
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }
}

/// <summary>One member row of a group card.</summary>
/// <param name="Detail">"MSI · 192.168.1.20" while reachable.</param>
public sealed record GroupMemberItem(string Name, bool IsHost, bool IsPhone, bool IsOnline, bool IsBusy, string StatusText, string Detail)
{
    public bool IsMe { get; init; }

    /// <summary>"Đặt tên gợi nhớ…" for that member, contact or not; null for ourselves.</summary>
    public ICommand? RenameCommand { get; init; }

    public bool CanRename => RenameCommand is not null;

    public bool HasDetail => Detail.Length > 0;
}

public sealed partial class GroupItemViewModel(Group group, string detail, IReadOnlyList<GroupMemberItem> members, GroupsTabViewModel owner)
{
    public string Id { get; } = group.Id;

    public string Name { get; } = group.Name;

    public string Detail { get; } = detail;

    public IReadOnlyList<GroupMemberItem> Members { get; } = members;

    /// <summary>Everything the card shows, to tell whether it needs rebuilding.</summary>
    internal string Signature => $"{Id}|{Name}|{Detail}|{group.Role}|{string.Join(";", Members)}";

    public bool CanSend { get; } = group.Role != GroupRole.Pending;

    public bool CanLeave { get; } = group.Role != GroupRole.Host;

    public string LeaveText { get; } = group.Role == GroupRole.Pending ? L.T("Hủy yêu cầu") : L.T("Rời nhóm");

    internal Task<string?> SendAsync(IReadOnlyList<string> locations) => owner.SendAsync(this, locations);

    [RelayCommand]
    private Task LeaveAsync() => owner.LeaveAsync(this);

    [RelayCommand]
    private void SendText() => owner.AskText(this);

    [RelayCommand]
    private void Chat() => owner.OpenChat(this);
}

public sealed partial class NetworkGroupItemViewModel(PresenceGroup group, DeviceId hostId, string hostName, GroupsTabViewModel owner)
{
    public string GroupId { get; } = group.Id;

    public string Name { get; } = group.Name;

    public DeviceId HostId { get; } = hostId;

    public string Detail { get; } = L.F("Host: {0} · {1} thành viên", hostName, group.Members);

    [RelayCommand]
    private Task JoinAsync() => owner.JoinAsync(this);
}

public sealed partial class InvitationItemViewModel(GroupInvitation invitation, GroupsTabViewModel owner)
{
    internal GroupInvitation Invitation { get; } = invitation;

    public string Text { get; } = L.F("{0} mời bạn vào nhóm {1}", invitation.HostName, invitation.GroupName);

    [RelayCommand]
    private Task AcceptAsync() => owner.AcceptAsync(this);

    [RelayCommand]
    private void Decline() => owner.Decline(this);
}
