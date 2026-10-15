using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;

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
    private readonly MainViewModel main;

    public GroupsTabViewModel(IServiceProvider services, MainViewModel main)
    {
        store = services.GetRequiredService<GroupStore>();
        groups = services.GetRequiredService<GroupService>();
        directory = services.GetRequiredService<PeerDirectory>();
        device = services.GetRequiredService<LocalDevice>();
        this.main = main;

        store.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        groups.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        groups.Notice += (_, notice) => Dispatcher.UIThread.Post(() => AddNotice(notice.Message));
        groups.InvitationReceived += (_, invitation) => Dispatcher.UIThread.Post(() =>
        {
            Refresh();
            main.Notice = $"{invitation.HostName} mời bạn vào nhóm {invitation.GroupName}: xem tab Nhóm.";
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

    internal Task<string?> SendAsync(GroupItemViewModel group, IReadOnlyList<string> locations) =>
        store.Get(group.Id) is { Role: not GroupRole.Pending } current
            ? main.SendAsync(locations, [.. current.Members.Where(m => m.DeviceId != device.DeviceId).Select(m => (m.DeviceId, (string?)current.Id))])
            : Task.FromResult<string?>("Chưa ở trong nhóm này.");

    internal Task LeaveAsync(GroupItemViewModel group) => RunAsync("Không rời nhóm được", () => groups.LeaveAsync(group.Id));

    internal Task JoinAsync(NetworkGroupItemViewModel group) =>
        RunAsync("Không gửi được yêu cầu", () => groups.RequestJoinAsync(group.HostId, group.GroupId, group.Name, null));

    internal Task AcceptAsync(InvitationItemViewModel invitation) =>
        RunAsync("Không vào nhóm được", () => groups.AcceptInvitationAsync(invitation.Invitation));

    internal void Decline(InvitationItemViewModel invitation) => groups.DismissInvitation(invitation.Invitation);

    private void Refresh()
    {
        IReadOnlyList<Group> all = store.GetAll();
        Replace(MyGroups, all.Select(g => new GroupItemViewModel(g, Describe(g), this)));

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
            return $"Host: {main.NameOf(group.HostId)} · đang chờ Host duyệt";
        }

        int online = group.Members.Count(m => m.DeviceId == device.DeviceId || directory.Snapshot().Any(p => p.DeviceId == m.DeviceId && p.State == PeerState.Online));
        string host = group.Role == GroupRole.Host ? "bạn" : main.NameOf(group.HostId);
        string unreachable = group.Role == GroupRole.Member && groups.IsHostReachable(group.Id) == false ? " · Host offline" : "";
        return $"Host: {host} · {group.Members.Count} thành viên · {online} online{unreachable}";
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
            main.Notice = $"{title}: không liên lạc được với Host.";
        }
        catch (PeerApiException ex)
        {
            main.Notice = $"{title}: " + ex.Status switch
            {
                404 => "nhóm này không còn.",
                429 => "đã gửi quá nhiều yêu cầu hoặc đã bị từ chối nhiều lần, thử lại sau.",
                503 => "nhóm đã đủ thành viên.",
                _ => "Host từ chối yêu cầu.",
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

public sealed partial class GroupItemViewModel(Group group, string detail, GroupsTabViewModel owner)
{
    public string Id { get; } = group.Id;

    public string Name { get; } = group.Name;

    public string Detail { get; } = detail;

    public string Members { get; } = string.Join(", ", group.Members.Select(m => m.IsHost ? $"{m.Name} (Host)" : m.Name));

    public bool CanSend { get; } = group.Role != GroupRole.Pending;

    public bool CanLeave { get; } = group.Role != GroupRole.Host;

    public string LeaveText { get; } = group.Role == GroupRole.Pending ? "Hủy yêu cầu" : "Rời nhóm";

    internal Task<string?> SendAsync(IReadOnlyList<string> locations) => owner.SendAsync(this, locations);

    [RelayCommand]
    private Task LeaveAsync() => owner.LeaveAsync(this);
}

public sealed partial class NetworkGroupItemViewModel(PresenceGroup group, DeviceId hostId, string hostName, GroupsTabViewModel owner)
{
    public string GroupId { get; } = group.Id;

    public string Name { get; } = group.Name;

    public DeviceId HostId { get; } = hostId;

    public string Detail { get; } = $"Host: {hostName} · {group.Members} thành viên";

    [RelayCommand]
    private Task JoinAsync() => owner.JoinAsync(this);
}

public sealed partial class InvitationItemViewModel(GroupInvitation invitation, GroupsTabViewModel owner)
{
    internal GroupInvitation Invitation { get; } = invitation;

    public string Text { get; } = $"{invitation.HostName} mời bạn vào nhóm {invitation.GroupName}";

    [RelayCommand]
    private Task AcceptAsync() => owner.AcceptAsync(this);

    [RelayCommand]
    private void Decline() => owner.Decline(this);
}
