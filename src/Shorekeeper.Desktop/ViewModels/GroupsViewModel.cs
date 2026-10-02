using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>A card files can be dropped on: a person or a group.</summary>
public interface IFileDropTarget
{
    bool CanDrop { get; }

    bool IsDropTarget { get; set; }

    Task DropAsync(IReadOnlyList<string> paths);
}

/// <summary>
/// The "Nhóm" page (docs/10-ux.md §6): groups we host or belong to, groups announced in the network,
/// invitations, and what happened lately ("Bạn đã vào nhóm …").
/// </summary>
public sealed partial class GroupsViewModel : ObservableObject
{
    private readonly GroupStore store;
    private readonly GroupService groups;
    private readonly PeerDirectory directory;
    private readonly LocalDevice device;
    private readonly LocalPresence presence;
    private readonly DialogService dialogs;
    private readonly Dictionary<string, GroupCardViewModel> cards = [];

    public GroupsViewModel(
        GroupStore store, GroupService groups, PeerDirectory directory, PeerNames names, LocalDevice device, LocalPresence presence, DialogService dialogs)
    {
        this.store = store;
        this.groups = groups;
        this.directory = directory;
        this.device = device;
        this.presence = presence;
        this.dialogs = dialogs;
        Names = names;

        store.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        groups.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        groups.Notice += (_, notice) => Dispatcher.UIThread.Post(() => AddNotice(notice.Message));
        // Requests and invitations arrive on API threads; the requester waits, so ask even from the tray.
        groups.JoinRequested += (_, request) => Dispatcher.UIThread.Post(() => AskAboutRequest(request));
        groups.InvitationReceived += (_, invitation) => Dispatcher.UIThread.Post(() => AskAboutInvitation(invitation));
        Refresh();
    }

    public ObservableCollection<GroupCardViewModel> MyGroups { get; } = [];

    public ObservableCollection<NetworkGroupViewModel> NetworkGroups { get; } = [];

    public ObservableCollection<InvitationViewModel> Invitations { get; } = [];

    public ObservableCollection<NoticeViewModel> Notices { get; } = [];

    public bool IsEnabled => groups.IsEnabled;

    public bool HasMyGroups => MyGroups.Count > 0;

    public bool HasNetworkGroups => NetworkGroups.Count > 0;

    /// <summary>Join requests waiting for us (as Host) plus invitations; shown as a badge.</summary>
    [ObservableProperty]
    public partial int AttentionCount { get; set; }

    internal PeerNames Names { get; }

    internal DeviceId Me => device.DeviceId;

    [RelayCommand]
    private async Task CreateAsync()
    {
        string? name = await DialogService.PromptAsync("Tạo nhóm", "Tên nhóm:", "");
        if (!string.IsNullOrWhiteSpace(name))
        {
            await RunAsync("Không tạo được nhóm", () => groups.CreateAsync(name));
        }
    }

    // ───────────── Actions used by the cards ─────────────

    internal async Task SendAsync(GroupCardViewModel card, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || store.Get(card.Id) is not { Role: not GroupRole.Pending } group)
        {
            return;
        }

        PickablePeer[] members = [.. group.Members.Where(m => m.DeviceId != Me).Select(m => Person(m.DeviceId, m.Name, isChecked: true))];
        IReadOnlyList<PickablePeer>? picked = await DialogService.PickPeopleAsync(
            $"Gửi cho nhóm {group.Name}", "Gửi", "Nhóm chưa có ai khác ngoài bạn.", members);
        if (picked is not null)
        {
            await dialogs.SendAsync(paths, [.. picked.Select(p => (p.DeviceId, p.Name))], group.Id);
        }
    }

    internal async Task InviteAsync(GroupCardViewModel card)
    {
        if (store.Get(card.Id) is not { Role: GroupRole.Host } group)
        {
            return;
        }

        PickablePeer[] candidates =
        [
            .. directory.Snapshot()
                .Where(p => p.State == PeerState.Online && !group.Contains(p.DeviceId))
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => Person(p.DeviceId, Names.Of(p.DeviceId), isChecked: false, p.HostName)),
        ];
        IReadOnlyList<PickablePeer>? picked = await DialogService.PickPeopleAsync(
            $"Mời vào nhóm {group.Name}", "Mời", "Không có ai đang online ngoài các thành viên.", candidates);
        foreach (PickablePeer person in picked ?? [])
        {
            string host = directory.Snapshot().FirstOrDefault(p => p.DeviceId == person.DeviceId)?.HostName ?? "";
            if (!await RunAsync($"Không mời được {person.Name}", () => groups.InviteAsync(group.Id, person.DeviceId, person.Name, host)))
            {
                break;
            }
        }
    }

    internal async Task RenameAsync(GroupCardViewModel card)
    {
        string? name = await DialogService.PromptAsync("Đổi tên nhóm", "Tên mới:", card.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await RunAsync("Không đổi tên được", () => groups.RenameAsync(card.Id, name));
        }
    }

    internal async Task CloseAsync(GroupCardViewModel card)
    {
        int choice = await DialogService.ChooseAsync(
            $"Đóng nhóm {card.Name}?",
            "Mọi thành viên sẽ rời nhóm. File đã gửi/nhận trước đó không bị ảnh hưởng.",
            ["Đóng nhóm", "Hủy"]);
        if (choice == 0)
        {
            await RunAsync("Không đóng được nhóm", () => groups.CloseAsync(card.Id));
        }
    }

    internal Task LeaveAsync(GroupCardViewModel card) => RunAsync("Không rời nhóm được", () => groups.LeaveAsync(card.Id));

    internal async Task RemoveAsync(GroupCardViewModel card, MemberViewModel member)
    {
        int choice = await DialogService.ChooseAsync(
            $"Loại {member.Name} khỏi nhóm?",
            $"{member.Name} sẽ không còn trong nhóm {card.Name} và không nhận file qua nhóm này nữa.",
            ["Loại khỏi nhóm", "Hủy"]);
        if (choice == 0)
        {
            await RunAsync($"Không loại được {member.Name}", () => groups.RemoveMemberAsync(card.Id, member.DeviceId));
        }
    }

    internal Task ApproveAsync(JoinRequestViewModel request) =>
        RunAsync("Không duyệt được", () => groups.ApproveAsync(request.GroupId, request.PeerId));

    internal Task DeclineAsync(JoinRequestViewModel request) =>
        RunAsync("Không từ chối được", () => groups.DeclineAsync(request.GroupId, request.PeerId));

    internal async Task JoinAsync(NetworkGroupViewModel group)
    {
        string? note = await DialogService.PromptAsync($"Xin vào nhóm {group.Name}", $"Lời nhắn cho {group.HostName} (không bắt buộc):", "");
        if (note is not null)
        {
            await RunAsync("Không gửi được yêu cầu", () => groups.RequestJoinAsync(group.HostId, group.GroupId, group.Name, note));
        }
    }

    internal Task AcceptAsync(GroupInvitation invitation) =>
        RunAsync("Không vào nhóm được", () => groups.AcceptInvitationAsync(invitation));

    internal void Decline(GroupInvitation invitation) => groups.DismissInvitation(invitation);

    internal void Dismiss(NoticeViewModel notice) => Notices.Remove(notice);

    // ───────────── Refresh ─────────────

    private void Refresh()
    {
        IReadOnlyList<Group> all = store.GetAll();
        foreach (string gone in cards.Keys.Except(all.Select(g => g.Id)).ToList())
        {
            cards.Remove(gone);
        }

        List<GroupCardViewModel> mine = [.. all.Select(g => Card(g.Id).Update(g, store.GetRequests(g.Id), groups.IsHostReachable(g.Id)))];
        if (!MyGroups.SequenceEqual(mine))
        {
            MyGroups.Clear();
            foreach (GroupCardViewModel card in mine)
            {
                MyGroups.Add(card);
            }
        }

        HashSet<string> known = [.. all.Select(g => g.Id)];
        NetworkGroups.Clear();
        foreach (PeerInfo host in directory.Snapshot())
        {
            foreach (PresenceGroup group in host.HostedGroups ?? [])
            {
                if (!known.Contains(group.Id))
                {
                    NetworkGroups.Add(new NetworkGroupViewModel(group.Id, group.Name, host.DeviceId, Names.Of(host.DeviceId), group.Members, this));
                }
            }
        }

        Invitations.Clear();
        foreach (GroupInvitation invitation in groups.GetInvitations())
        {
            Invitations.Add(new InvitationViewModel(invitation, this));
        }

        AttentionCount = mine.Sum(c => c.Requests.Count(r => !r.IsInvited)) + Invitations.Count;
        OnPropertyChanged(nameof(HasMyGroups));
        OnPropertyChanged(nameof(HasNetworkGroups));
        OnPropertyChanged(nameof(IsEnabled));
    }

    private GroupCardViewModel Card(string id)
    {
        if (!cards.TryGetValue(id, out GroupCardViewModel? card))
        {
            card = new GroupCardViewModel(id, this);
            cards[id] = card;
        }

        return card;
    }

    /// <summary>Alias or presence name when we know the peer, else the name the group gave us.</summary>
    internal string NameOf(DeviceId id, string fallback)
    {
        string name = Names.Of(id);
        return name == id.ShortForm && fallback.Length > 0 ? fallback : name;
    }

    private PickablePeer Person(DeviceId id, string fallbackName, bool isChecked, string? host = null) =>
        new(id, NameOf(id, fallbackName), Names.IsOnline(id) ? host ?? "online" : "offline", isChecked);

    private void AskAboutRequest(JoinRequest request)
    {
        if (presence.Status == MyStatus.Busy || store.Get(request.GroupId) is not { } group)
        {
            return;
        }

        string note = request.Note is null ? "" : $"\n\n\"{request.Note}\"";
        DialogService.ShowPopup(
            $"{request.Name} xin vào nhóm",
            $"{request.Name} ({request.Host}) xin vào nhóm {group.Name}.{note}",
            ["Duyệt", "Từ chối", "Để sau"],
            choice =>
            {
                if (choice == 0)
                {
                    _ = RunAsync("Không duyệt được", () => groups.ApproveAsync(request.GroupId, request.PeerId));
                }
                else if (choice == 1)
                {
                    _ = RunAsync("Không từ chối được", () => groups.DeclineAsync(request.GroupId, request.PeerId));
                }
            });
    }

    private void AskAboutInvitation(GroupInvitation invitation)
    {
        if (presence.Status == MyStatus.Busy)
        {
            return;
        }

        DialogService.ShowPopup(
            "Lời mời vào nhóm",
            $"{invitation.HostName} mời bạn vào nhóm {invitation.GroupName}.",
            ["Đồng ý", "Từ chối", "Để sau"],
            choice =>
            {
                if (choice == 0)
                {
                    _ = AcceptAsync(invitation);
                }
                else if (choice == 1)
                {
                    Decline(invitation);
                }
            });
    }

    private void AddNotice(string message)
    {
        Notices.Insert(0, new NoticeViewModel($"{DateTime.Now:HH:mm} · {message}", this));
        while (Notices.Count > 5)
        {
            Notices.RemoveAt(Notices.Count - 1);
        }
    }

    /// <summary>Runs a group action; failures are explained in plain words.</summary>
    /// <returns>False when it failed.</returns>
    private static async Task<bool> RunAsync(string title, Func<Task> action)
    {
        string? error;
        try
        {
            await action();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }
        catch (PeerUnreachableException)
        {
            error = "Không liên lạc được với máy kia. Máy đó có thể đang tắt Shorekeeper hoặc ở ngoài mạng.";
        }
        catch (PeerApiException ex)
        {
            error = ex.Status switch
            {
                404 => "Nhóm này không còn (hoặc máy kia đã tắt tính năng nhóm).",
                429 => "Đã gửi quá nhiều yêu cầu hoặc đã bị từ chối nhiều lần. Hãy thử lại sau.",
                503 => "Nhóm đã đủ thành viên.",
                _ => "Máy kia từ chối yêu cầu.",
            };
        }

        await DialogService.ChooseAsync(title, error, ["Đóng"]);
        return false;
    }
}

public sealed partial class GroupCardViewModel(string id, GroupsViewModel owner) : ObservableObject, IFileDropTarget
{
    public string Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "";

    /// <summary>"Đang chờ Host duyệt", "Host offline — vẫn gửi file cho nhau được"…; null when nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; set; }

    public bool HasStatus => StatusText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDrop), nameof(CanLeave))]
    public partial bool IsHost { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDrop), nameof(LeaveText))]
    public partial bool IsPending { get; set; }

    public bool CanDrop => !IsPending;

    public bool CanLeave => !IsHost;

    public string LeaveText => IsPending ? "Hủy yêu cầu" : "Rời nhóm";

    [ObservableProperty]
    public partial IReadOnlyList<MemberViewModel> Members { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequests))]
    public partial IReadOnlyList<JoinRequestViewModel> Requests { get; set; } = [];

    public bool HasRequests => Requests.Count > 0;

    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    public Task DropAsync(IReadOnlyList<string> paths) => owner.SendAsync(this, paths);

    public GroupCardViewModel Update(Group group, IReadOnlyList<JoinRequest> requests, bool? hostReachable)
    {
        Name = group.Name;
        IsHost = group.Role == GroupRole.Host;
        IsPending = group.Role == GroupRole.Pending;

        Members =
        [
            .. group.Members
                .Select(m => new MemberViewModel(m.DeviceId, m.DeviceId == owner.Me ? "Bạn" : owner.NameOf(m.DeviceId, m.Name),
                    m.IsHost, m.DeviceId == owner.Me || owner.Names.IsOnline(m.DeviceId), IsHost && m.DeviceId != owner.Me, this, owner))
                .OrderByDescending(m => m.IsHost)
                .ThenBy(m => m.Name, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true)),
        ];
        Requests = IsHost
            ? [.. requests.Where(r => r.State != JoinRequestState.Declined).Select(r => new JoinRequestViewModel(r, owner))]
            : [];

        string hostName = IsHost ? "bạn" : owner.Names.Of(group.HostId);
        Subtitle = IsPending
            ? $"Host: {hostName}"
            : $"Host: {hostName} · {group.Members.Count} thành viên · {Members.Count(m => m.IsOnline)} online";
        StatusText = IsPending ? "Đang chờ Host duyệt yêu cầu của bạn."
            : !IsHost && hostReachable == false ? "Host offline: vẫn gửi file cho nhau được, chỉ chưa vào/ra thành viên được."
            : null;
        return this;
    }

    [RelayCommand]
    private async Task SendFilesAsync() => await owner.SendAsync(this, await DialogService.PickFilesAsync());

    [RelayCommand]
    private async Task SendFolderAsync() => await owner.SendAsync(this, await DialogService.PickFolderAsync());

    [RelayCommand]
    private Task InviteAsync() => owner.InviteAsync(this);

    [RelayCommand]
    private Task RenameAsync() => owner.RenameAsync(this);

    [RelayCommand]
    private Task CloseAsync() => owner.CloseAsync(this);

    [RelayCommand]
    private Task LeaveAsync() => owner.LeaveAsync(this);
}

public sealed partial class MemberViewModel(
    DeviceId deviceId, string name, bool isHost, bool isOnline, bool canRemove, GroupCardViewModel card, GroupsViewModel owner)
{
    public DeviceId DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public bool IsHost { get; } = isHost;

    public bool IsOnline { get; } = isOnline;

    public bool CanRemove { get; } = canRemove;

    public string Label => IsHost ? $"{Name} (Host)" : Name;

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(card, this);
}

public sealed partial class JoinRequestViewModel(JoinRequest request, GroupsViewModel owner)
{
    public string GroupId { get; } = request.GroupId;

    public DeviceId PeerId { get; } = request.PeerId;

    public bool IsInvited { get; } = request.State == JoinRequestState.Invited;

    public string Text { get; } = request.State == JoinRequestState.Invited
        ? $"Đã mời {request.Name}, chờ đồng ý"
        : $"{request.Name} ({request.Host}) xin vào" + (request.Note is null ? "" : $": \"{request.Note}\"");

    [RelayCommand]
    private Task ApproveAsync() => owner.ApproveAsync(this);

    [RelayCommand]
    private Task DeclineAsync() => owner.DeclineAsync(this);
}

/// <summary>A group someone else hosts, seen in their presence; we are not in it.</summary>
public sealed partial class NetworkGroupViewModel(string groupId, string name, DeviceId hostId, string hostName, int members, GroupsViewModel owner)
{
    public string GroupId { get; } = groupId;

    public string Name { get; } = name;

    public DeviceId HostId { get; } = hostId;

    public string HostName { get; } = hostName;

    public string Detail { get; } = $"Host: {hostName} · {members} thành viên";

    [RelayCommand]
    private Task JoinAsync() => owner.JoinAsync(this);
}

public sealed partial class InvitationViewModel(GroupInvitation invitation, GroupsViewModel owner)
{
    public string Text { get; } = $"{invitation.HostName} mời bạn vào nhóm {invitation.GroupName}";

    [RelayCommand]
    private Task AcceptAsync() => owner.AcceptAsync(invitation);

    [RelayCommand]
    private void Decline() => owner.Decline(invitation);
}

public sealed partial class NoticeViewModel(string text, GroupsViewModel owner)
{
    public string Text { get; } = text;

    [RelayCommand]
    private void Dismiss() => owner.Dismiss(this);
}
