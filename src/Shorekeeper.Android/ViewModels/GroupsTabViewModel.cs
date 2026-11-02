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
/// "Nhóm" on the phone (docs/14-android.md, A5; docs/20-phone-host.md): groups we are in (send to members, leave),
/// groups we host (requests, invite, edit, remove members, close), groups announced in the network ([Xin vào]),
/// invitations, and what happened lately.
/// </summary>
public sealed partial class GroupsTabViewModel : ObservableObject
{
    /// <summary>A phone hosts at most this many groups (docs/20-phone-host.md §6 Q5); a computer 10.</summary>
    public const int MaxHosted = 3;

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
        // The notification asks too (Notifications.ShowJoinRequest); this is for when the app is open.
        groups.JoinRequested += (_, request) => Dispatcher.UIThread.Post(() =>
        {
            Refresh();
            main.Notice = L.F("{0} xin vào nhóm {1}: xem tab Nhóm.", request.Name, store.Get(request.GroupId)?.Name ?? "");
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

    /// <summary>Someone asks to join a group we host, or invites us: a dot on the tab.</summary>
    [ObservableProperty]
    public partial bool NeedsAttention { get; set; }

    /// <summary>docs/16-group-chat.md §3.7, said for a phone (docs/20-phone-host.md §6 Q3).</summary>
    private static string HostOnlyText =>
        L.T("Chỉ nhắn khi Host online: điện thoại này tắt hay ngủ thì cả nhóm không nhắn được, đổi lại người bị loại không chen được tin nào");

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

    // ───────────── Hosting (docs/20-phone-host.md, H2) ─────────────

    private int HostedCount => store.GetAll().Count(g => g.Role == GroupRole.Host);

    /// <summary>
    /// "Tạo nhóm": a Host must stay reachable, so "Chạy nền" comes on first (docs/20-phone-host.md §6 Q1), then the name
    /// and whether the group may only talk while we are online.
    /// </summary>
    [RelayCommand]
    private void Create()
    {
        if (HostedCount >= MaxHosted)
        {
            main.Notice = L.F("Điện thoại làm Host tối đa {0} nhóm.", MaxHosted);
            return;
        }

        if (BackgroundTransfers.AlwaysReceive)
        {
            AskNewGroup();
            return;
        }

        main.Question = new QuestionViewModel(
            L.T("Bật Chạy nền?"),
            L.T("Người khác chỉ xin vào, được duyệt và biết ai bị loại khi Host online. Bật Chạy nền để điện thoại vẫn làm Host khi tắt màn hình."),
            L.T("Bật và tiếp tục"), null,
            _ =>
            {
                main.Settings.AlwaysReceive = true;
                AskNewGroup();
                return Task.CompletedTask;
            }, main);
    }

    private void AskNewGroup()
    {
        QuestionViewModel? question = null;
        question = new QuestionViewModel(L.T("Tạo nhóm"), L.T("Tên nhóm:"), L.T("Tạo"), "",
            name => string.IsNullOrWhiteSpace(name) ? Task.CompletedTask : RunAsync(L.T("Không tạo được nhóm"), async () =>
            {
                Group group = await groups.CreateAsync(name);
                if (question!.OptionChecked)
                {
                    await groups.SetHostOnlyChatAsync(group.Id, true);
                }
            }), main)
        {
            Option = HostOnlyText,
        };
        main.Question = question;
    }

    internal void Edit(GroupItemViewModel card)
    {
        QuestionViewModel? question = null;
        question = new QuestionViewModel(L.T("Sửa nhóm"), L.T("Tên nhóm:"), L.T("Lưu"), card.Name,
            name => string.IsNullOrWhiteSpace(name) ? Task.CompletedTask : RunAsync(L.T("Không sửa được nhóm"), async () =>
            {
                await groups.RenameAsync(card.Id, name);
                await groups.SetHostOnlyChatAsync(card.Id, question!.OptionChecked);
            }), main)
        {
            Option = HostOnlyText,
            OptionChecked = store.Get(card.Id)?.HostOnlyChat ?? false,
        };
        main.Question = question;
    }

    internal void Close(GroupItemViewModel card) =>
        main.Question = new QuestionViewModel(
            L.F("Đóng nhóm {0}?", card.Name), L.T("Mọi thành viên sẽ rời nhóm. File đã gửi/nhận trước đó không bị ảnh hưởng."), L.T("Đóng nhóm"), null,
            _ => RunAsync(L.T("Không đóng được nhóm"), () => groups.CloseAsync(card.Id)), main);

    /// <summary>"Mời…": people online who are not in the group yet.</summary>
    internal void Invite(GroupItemViewModel card)
    {
        if (store.Get(card.Id) is not { Role: GroupRole.Host } group)
        {
            return;
        }

        PickablePerson[] candidates =
        [
            .. directory.Snapshot()
                .Where(p => p.State == PeerState.Online && !group.Contains(p.DeviceId))
                .OrderBy(p => main.NameOf(p.DeviceId), StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new PickablePerson(p.DeviceId, main.NameOf(p.DeviceId), $"{p.HostName} · {p.Address}", null, p)),
        ];
        main.Invite = new InviteViewModel(group.Name, candidates, async picked =>
        {
            foreach (PickablePerson person in picked)
            {
                if (!await InviteAsync(group.Id, person.DeviceId, person.Name))
                {
                    break;
                }
            }
        }, main);
    }

    /// <summary>The groups we host <paramref name="peer"/> is not in: "Mời vào nhóm…" on their card in Xóm.</summary>
    internal IReadOnlyList<Group> OpenTo(DeviceId peer) =>
        groups.IsEnabled ? [.. store.GetAll().Where(g => g.Role == GroupRole.Host && !g.Contains(peer)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)] : [];

    /// <summary>"Mời vào nhóm…" on a person's card in Xóm: into one of the groups we host they are not in yet.</summary>
    internal void InviteFromCard(DeviceId peer, string name)
    {
        IReadOnlyList<Group> open = OpenTo(peer);
        if (open.Count == 0)
        {
            return;
        }

        QuestionViewModel? question = null;
        question = new QuestionViewModel(L.F("Mời {0} vào nhóm", name), L.T("Chọn nhóm:"), L.T("Mời"), null,
            _ => question!.Choice >= 0 && question.Choice < open.Count ? InviteAsync(open[question.Choice].Id, peer, name) : Task.CompletedTask, main)
        {
            Choices = [.. open.Select(g => g.Name)],
        };
        main.Question = question;
    }

    private Task<bool> InviteAsync(string groupId, DeviceId peer, string name)
    {
        string host = directory.Snapshot().FirstOrDefault(p => p.DeviceId == peer)?.HostName ?? "";
        return RunAsync(L.F("Không mời được {0}", name), () => groups.InviteAsync(groupId, peer, name, host));
    }

    internal Task ApproveAsync(JoinRequestItem request) =>
        RunAsync(L.T("Không duyệt được"), () => groups.ApproveAsync(request.GroupId, request.PeerId));

    internal Task DeclineAsync(JoinRequestItem request) =>
        RunAsync(L.T("Không từ chối được"), () => groups.DeclineAsync(request.GroupId, request.PeerId));

    private void AskRemove(Group group, DeviceId member, string name) =>
        main.Question = new QuestionViewModel(
            L.F("Loại {0} khỏi nhóm?", name), L.F("{0} sẽ không còn trong nhóm {1} và không nhận file qua nhóm này nữa.", name, group.Name),
            L.T("Loại khỏi nhóm"), null,
            _ => RunAsync(L.F("Không loại được {0}", name), () => groups.RemoveMemberAsync(group.Id, member)), main);

    private void Refresh()
    {
        IReadOnlyList<Group> all = store.GetAll();
        GroupItemViewModel[] mine =
        [
            .. all.Select(g => new GroupItemViewModel(g, Describe(g), [.. Members(g)], Requests(g), this)),
        ];
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

        NeedsAttention = Invitations.Count > 0 || mine.Any(g => g.Requests.Any(r => !r.IsInvited));
        OnPropertyChanged(nameof(HasMyGroups));
        OnPropertyChanged(nameof(HasNetworkGroups));
        OnPropertyChanged(nameof(HasInvitations));
        OnPropertyChanged(nameof(IsEnabled));
    }

    /// <summary>Waiting for us, as Host: asks to join first, then those we invited who have not answered.</summary>
    private IReadOnlyList<JoinRequestItem> Requests(Group group) =>
        group.Role != GroupRole.Host ? []
            : [.. store.GetRequests(group.Id)
                .Where(r => r.State is JoinRequestState.Pending or JoinRequestState.Invited)
                .OrderBy(r => r.State == JoinRequestState.Invited)
                .Select(r => new JoinRequestItem(r, this))];

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
                string name = isMe ? L.T("Bạn") : main.NameOf(m.DeviceId, m.Name);
                return new GroupMemberItem(
                    name,
                    m.IsHost,
                    isMe ? true : peer is not null ? peer.Os.Equals("android", StringComparison.OrdinalIgnoreCase) : trust.Get(m.DeviceId)?.IsPhone == true,
                    isOnline,
                    peer?.IsBusy == true,
                    isOnline ? peer?.IsBusy == true ? L.T("Bận") : "Online" : peer is null ? "Offline" : L.T("Mất tín hiệu…"),
                    isMe ? "" : peer is null ? "" : $"{peer.HostName} · {peer.Address}")
                {
                    IsMe = isMe,
                    RenameCommand = isMe ? null : new AsyncRelayCommand(() => main.Chat.RenameAsync(m.DeviceId, m.Name)),
                    RemoveCommand = isMe || group.Role != GroupRole.Host ? null : new RelayCommand(() => AskRemove(group, m.DeviceId, name)),
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

    /// <summary>Runs a group action; failures are explained in plain words, as on the desktop. False when it failed.</summary>
    private async Task<bool> RunAsync(string title, Func<Task> action)
    {
        try
        {
            await action();
            return true;
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

        return false;
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

    /// <summary>"Loại khỏi nhóm" in a group we host; null for ourselves and in others' groups.</summary>
    public ICommand? RemoveCommand { get; init; }

    public bool CanRemove => RemoveCommand is not null;

    public bool HasDetail => Detail.Length > 0;
}

/// <summary>Someone asking to join a group we host, or someone we invited who has not answered yet.</summary>
public sealed partial class JoinRequestItem(JoinRequest request, GroupsTabViewModel owner)
{
    public string GroupId { get; } = request.GroupId;

    public DeviceId PeerId { get; } = request.PeerId;

    public bool IsInvited { get; } = request.State == JoinRequestState.Invited;

    public bool IsAsking => !IsInvited;

    public string Text { get; } = request.State == JoinRequestState.Invited
        ? L.F("Đã mời {0}, chờ đồng ý", request.Name)
        : L.F("{0} ({1}) xin vào", request.Name, request.Host) + (request.Note is null ? "" : $": \"{request.Note}\"");

    public override string ToString() => $"{PeerId}:{IsInvited}";

    [RelayCommand]
    private Task ApproveAsync() => owner.ApproveAsync(this);

    [RelayCommand]
    private Task DeclineAsync() => owner.DeclineAsync(this);
}

public sealed partial class GroupItemViewModel(
    Group group, string detail, IReadOnlyList<GroupMemberItem> members, IReadOnlyList<JoinRequestItem> requests, GroupsTabViewModel owner)
{
    public string Id { get; } = group.Id;

    public string Name { get; } = group.Name;

    public string Detail { get; } = detail;

    public IReadOnlyList<GroupMemberItem> Members { get; } = members;

    public IReadOnlyList<JoinRequestItem> Requests { get; } = requests;

    public bool HasRequests => Requests.Count > 0;

    /// <summary>Everything the card shows, to tell whether it needs rebuilding.</summary>
    internal string Signature => $"{Id}|{Name}|{Detail}|{group.Role}|{string.Join(";", Members)}|{string.Join(";", Requests)}";

    public bool IsHost { get; } = group.Role == GroupRole.Host;

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

    [RelayCommand]
    private void Invite() => owner.Invite(this);

    [RelayCommand]
    private void Edit() => owner.Edit(this);

    [RelayCommand]
    private void Close() => owner.Close(this);
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
