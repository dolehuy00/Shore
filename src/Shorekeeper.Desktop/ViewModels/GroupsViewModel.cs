using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
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
        names.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
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
        if (await DialogService.PromptWithOptionAsync(L.T("Tạo nhóm"), L.T("Tên nhóm:"), "", HostOnlyText, false) is { } answer
            && !string.IsNullOrWhiteSpace(answer.Text))
        {
            await RunAsync(L.T("Không tạo được nhóm"), async () =>
            {
                Group group = await groups.CreateAsync(answer.Text);
                if (answer.Option)
                {
                    await groups.SetHostOnlyChatAsync(group.Id, true);
                }
            });
        }
    }

    /// <summary>docs/16-group-chat.md §3.7.</summary>
    private static string HostOnlyText => L.T("Chỉ nhắn khi Host online: Host tắt thì cả nhóm không nhắn được, đổi lại người bị loại không chen được tin nào");

    // ───────────── Actions used by the cards ─────────────

    /// <summary>"Nhắn tin" on a group: the window opens its conversation.</summary>
    public event EventHandler<string>? ChatRequested;

    internal void RequestChat(GroupCardViewModel card) => ChatRequested?.Invoke(this, card.Id);

    internal async Task SendAsync(GroupCardViewModel card, IReadOnlyList<string> paths, string? text = null)
    {
        if ((paths.Count == 0 && string.IsNullOrWhiteSpace(text)) || store.Get(card.Id) is not { Role: not GroupRole.Pending } group)
        {
            return;
        }

        PickablePeer[] members = [.. group.Members.Where(m => m.DeviceId != Me).Select(m => Person(m.DeviceId, m.Name, isChecked: true))];
        IReadOnlyList<PickablePeer>? picked = await DialogService.PickPeopleAsync(
            L.F("Gửi cho nhóm {0}", group.Name), L.T("Gửi"), L.T("Nhóm chưa có ai khác ngoài bạn."), members);
        if (picked is not null)
        {
            await dialogs.SendAsync(paths, [.. picked.Select(p => (p.DeviceId, p.Name))], group.Id, text);
        }
    }

    internal async Task SendTextAsync(GroupCardViewModel card)
    {
        if (await DialogService.PromptAsync(L.T("Gửi văn bản"), L.F("Văn bản hoặc link gửi cho nhóm {0}:", card.Name), "", L.T("Tiếp"), multiline: true) is { } text)
        {
            await SendAsync(card, [], text);
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
            L.F("Mời vào nhóm {0}", group.Name), L.T("Mời"), L.T("Không có ai đang online ngoài các thành viên."), candidates);
        foreach (PickablePeer person in picked ?? [])
        {
            string host = directory.Snapshot().FirstOrDefault(p => p.DeviceId == person.DeviceId)?.HostName ?? "";
            if (!await RunAsync(L.F("Không mời được {0}", person.Name), () => groups.InviteAsync(group.Id, person.DeviceId, person.Name, host)))
            {
                break;
            }
        }
    }

    internal async Task EditAsync(GroupCardViewModel card)
    {
        bool hostOnly = store.Get(card.Id)?.HostOnlyChat ?? false;
        if (await DialogService.PromptWithOptionAsync(L.T("Sửa nhóm"), L.T("Tên nhóm:"), card.Name, HostOnlyText, hostOnly) is { } answer
            && !string.IsNullOrWhiteSpace(answer.Text))
        {
            await RunAsync(L.T("Không sửa được nhóm"), async () =>
            {
                await groups.RenameAsync(card.Id, answer.Text);
                await groups.SetHostOnlyChatAsync(card.Id, answer.Option);
            });
        }
    }

    internal async Task CloseAsync(GroupCardViewModel card)
    {
        int choice = await DialogService.ChooseAsync(
            L.F("Đóng nhóm {0}?", card.Name),
            L.T("Mọi thành viên sẽ rời nhóm. File đã gửi/nhận trước đó không bị ảnh hưởng."),
            [L.T("Đóng nhóm"), L.T("Hủy")]);
        if (choice == 0)
        {
            await RunAsync(L.T("Không đóng được nhóm"), () => groups.CloseAsync(card.Id));
        }
    }

    internal Task LeaveAsync(GroupCardViewModel card) => RunAsync(L.T("Không rời nhóm được"), () => groups.LeaveAsync(card.Id));

    internal async Task RemoveAsync(GroupCardViewModel card, MemberViewModel member)
    {
        int choice = await DialogService.ChooseAsync(
            L.F("Loại {0} khỏi nhóm?", member.Name),
            L.F("{0} sẽ không còn trong nhóm {1} và không nhận file qua nhóm này nữa.", member.Name, card.Name),
            [L.T("Loại khỏi nhóm"), L.T("Hủy")]);
        if (choice == 0)
        {
            await RunAsync(L.F("Không loại được {0}", member.Name), () => groups.RemoveMemberAsync(card.Id, member.DeviceId));
        }
    }

    internal Task ApproveAsync(JoinRequestViewModel request) =>
        RunAsync(L.T("Không duyệt được"), () => groups.ApproveAsync(request.GroupId, request.PeerId));

    internal Task DeclineAsync(JoinRequestViewModel request) =>
        RunAsync(L.T("Không từ chối được"), () => groups.DeclineAsync(request.GroupId, request.PeerId));

    internal async Task JoinAsync(NetworkGroupViewModel group)
    {
        string? note = await DialogService.PromptAsync(L.F("Xin vào nhóm {0}", group.Name), L.F("Lời nhắn cho {0} (không bắt buộc):", group.HostName), "");
        if (note is not null)
        {
            await RunAsync(L.T("Không gửi được yêu cầu"), () => groups.RequestJoinAsync(group.HostId, group.GroupId, group.Name, note));
        }
    }

    internal Task AcceptAsync(GroupInvitation invitation) =>
        RunAsync(L.T("Không vào nhóm được"), () => groups.AcceptInvitationAsync(invitation));

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

        // Rebuilt only when something shown changed: this runs whenever a device appears or goes quiet.
        HashSet<string> known = [.. all.Select(g => g.Id)];
        var networkGroups = directory.Snapshot()
            .SelectMany(host => (host.HostedGroups ?? []).Select(group => (host, group)))
            .Where(x => !known.Contains(x.group.Id))
            .Select(x => (x.group.Id, x.group.Name, x.host.DeviceId, Host: Names.Of(x.host.DeviceId), x.group.Members))
            .ToList();
        if (!networkGroups.SequenceEqual(NetworkGroups.Select(n => (n.GroupId, n.Name, n.HostId, n.HostName, n.Members))))
        {
            NetworkGroups.Clear();
            foreach (var group in networkGroups)
            {
                NetworkGroups.Add(new NetworkGroupViewModel(group.Id, group.Name, group.DeviceId, group.Host, group.Members, this));
            }
        }

        IReadOnlyList<GroupInvitation> invitations = groups.GetInvitations();
        if (!invitations.SequenceEqual(Invitations.Select(i => i.Invitation)))
        {
            Invitations.Clear();
            foreach (GroupInvitation invitation in invitations)
            {
                Invitations.Add(new InvitationViewModel(invitation, this));
            }
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
    internal string NameOf(DeviceId id, string fallback) => Names.Of(id, fallback);

    private PickablePeer Person(DeviceId id, string fallbackName, bool isChecked, string? host = null) =>
        new(id, NameOf(id, fallbackName), Names.IsOnline(id) ? host ?? "online" : "offline", isChecked);

    private void AskAboutRequest(JoinRequest request)
    {
        if (presence.Status == MyStatus.Busy || store.Get(request.GroupId) is not { } group)
        {
            return;
        }

        string message = L.F("{0} ({1}) xin vào nhóm {2}.", request.Name, request.Host, group.Name);
        dialogs.Ask(
            L.F("{0} xin vào nhóm", request.Name),
            request.Note is null ? [message] : [message, $"\"{request.Note}\""],
            [L.T("Duyệt"), L.T("Từ chối")],
            NavPage.Groups,
            choice =>
            {
                if (choice == 0)
                {
                    _ = RunAsync(L.T("Không duyệt được"), () => groups.ApproveAsync(request.GroupId, request.PeerId));
                }
                else if (choice == 1)
                {
                    _ = RunAsync(L.T("Không từ chối được"), () => groups.DeclineAsync(request.GroupId, request.PeerId));
                }
            });
    }

    private void AskAboutInvitation(GroupInvitation invitation)
    {
        if (presence.Status == MyStatus.Busy)
        {
            return;
        }

        dialogs.Ask(
            L.T("Lời mời vào nhóm"),
            [L.F("{0} mời bạn vào nhóm {1}.", invitation.HostName, invitation.GroupName)],
            [L.T("Đồng ý"), L.T("Từ chối")],
            NavPage.Groups,
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
            error = L.T("Không liên lạc được với máy kia. Máy đó có thể đang tắt Shorekeeper hoặc ở ngoài mạng.");
        }
        catch (PeerApiException ex)
        {
            error = ex.Status switch
            {
                404 => L.T("Nhóm này không còn (hoặc máy kia đã tắt tính năng nhóm)."),
                429 => L.T("Đã gửi quá nhiều yêu cầu hoặc đã bị từ chối nhiều lần. Hãy thử lại sau."),
                503 => L.T("Nhóm đã đủ thành viên."),
                _ => L.T("Máy kia từ chối yêu cầu."),
            };
        }

        await DialogService.ChooseAsync(title, error, [L.T("Đóng")]);
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

    public string LeaveText => IsPending ? L.T("Hủy yêu cầu") : L.T("Rời nhóm");

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
            .. group.Members.Select(m => Member(m))
                .OrderByDescending(m => m.IsHost)
                .ThenBy(m => m.Name, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true)),
        ];
        Requests = IsHost
            ? [.. requests.Where(r => r.State != JoinRequestState.Declined).Select(r => new JoinRequestViewModel(r, owner))]
            : [];

        string hostName = IsHost ? L.T("bạn") : owner.Names.Of(group.HostId);
        Subtitle = IsPending
            ? $"Host: {hostName}"
            : L.F("Host: {0} · {1} thành viên · {2} online", hostName, group.Members.Count, Members.Count(m => m.IsOnline));
        StatusText = IsPending ? L.T("Đang chờ Host duyệt yêu cầu của bạn.")
            : !IsHost && hostReachable == false ? L.T("Host offline: vẫn gửi file cho nhau được, chỉ chưa vào/ra thành viên được.")
            : null;
        return this;
    }

    /// <summary>A member row: who, on what device, whether they can be reached now, and where.</summary>
    private MemberViewModel Member(GroupMember member)
    {
        bool isMe = member.DeviceId == owner.Me;
        PeerInfo? live = isMe ? null : owner.Names.Live(member.DeviceId);
        bool isOnline = isMe || live?.State == PeerState.Online;
        string detail = isMe ? LocalDevice.HostName
            : live is null ? ""
            : $"{live.HostName} · {live.Address}";
        return new MemberViewModel(member.DeviceId, isMe ? L.T("Bạn") : owner.NameOf(member.DeviceId, member.Name),
            member.IsHost, isOnline, IsHost && !isMe, this, owner)
        {
            IsMe = isMe,
            KnownAs = member.Name,
            IsPhone = !isMe && owner.Names.IsPhone(member.DeviceId),
            IsBusy = live?.IsBusy == true,
            StatusText = isOnline ? live?.IsBusy == true ? L.T("Bận") : "Online" : live is null ? "Offline" : L.T("Mất tín hiệu…"),
            Detail = detail,
        };
    }

    [RelayCommand]
    private void Chat() => owner.RequestChat(this);

    [RelayCommand]
    private async Task SendFilesAsync() => await owner.SendAsync(this, await DialogService.PickFilesAsync());

    [RelayCommand]
    private async Task SendFolderAsync() => await owner.SendAsync(this, await DialogService.PickFolderAsync());

    [RelayCommand]
    private Task SendTextAsync() => owner.SendTextAsync(this);

    [RelayCommand]
    private Task InviteAsync() => owner.InviteAsync(this);

    [RelayCommand]
    private Task EditAsync() => owner.EditAsync(this);

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

    public bool IsMe { get; init; }

    public bool IsPhone { get; init; }

    public bool IsBusy { get; init; }

    /// <summary>"Online", "Bận", "Offline".</summary>
    public string StatusText { get; init; } = "";

    /// <summary>"MSI-PC · 192.168.1.20" while online; this computer's name for "Bạn".</summary>
    public string Detail { get; init; } = "";

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Their name in the group, offered when we have no name of our own for them.</summary>
    public string KnownAs { get; init; } = name;

    public bool CanRename => !IsMe;

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(card, this);

    [RelayCommand]
    private Task RenameAsync() => owner.Names.AskAliasAsync(DeviceId, KnownAs);
}

public sealed partial class JoinRequestViewModel(JoinRequest request, GroupsViewModel owner)
{
    public string GroupId { get; } = request.GroupId;

    public DeviceId PeerId { get; } = request.PeerId;

    public bool IsInvited { get; } = request.State == JoinRequestState.Invited;

    public string Text { get; } = request.State == JoinRequestState.Invited
        ? L.F("Đã mời {0}, chờ đồng ý", request.Name)
        : L.F("{0} ({1}) xin vào", request.Name, request.Host) + (request.Note is null ? "" : $": \"{request.Note}\"");

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

    public int Members { get; } = members;

    public string Detail { get; } = L.F("Host: {0} · {1} thành viên", hostName, members);

    [RelayCommand]
    private Task JoinAsync() => owner.JoinAsync(this);
}

public sealed partial class InvitationViewModel(GroupInvitation invitation, GroupsViewModel owner)
{
    public GroupInvitation Invitation { get; } = invitation;

    public string Text { get; } = L.F("{0} mời bạn vào nhóm {1}", invitation.HostName, invitation.GroupName);

    [RelayCommand]
    private Task AcceptAsync() => owner.AcceptAsync(Invitation);

    [RelayCommand]
    private void Decline() => owner.Decline(Invitation);
}

public sealed partial class NoticeViewModel(string text, GroupsViewModel owner)
{
    public string Text { get; } = text;

    [RelayCommand]
    private void Dismiss() => owner.Dismiss(this);
}
