using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Groups;

/// <summary>A Host invited us into one of its groups; the UI asks whether to join.</summary>
public sealed record GroupInvitation(string GroupId, string GroupName, DeviceId HostId, string HostName);

/// <summary>Something happened to one of our groups that the user should hear about.</summary>
public sealed record GroupNotice(string GroupName, string Message);

public enum JoinRequestResult
{
    Pending,

    /// <summary>Already a member, or the Host had invited the caller.</summary>
    Member,
    NotFound,
    RateLimited,
    Full,
    Invalid,
}

/// <summary>
/// Groups over the network (docs/07-groups.md). Host side: join requests, approvals, invitations, and the member list
/// that members long-poll (<c>GET /groups/{id}/state</c>, like the Bridge registry, ADR-011). Member side: asking to
/// join, leaving, and one poll loop per group that keeps the cached member list fresh and feeds member addresses
/// to discovery, so members in other subnets see each other.
/// </summary>
public sealed class GroupService(
    GroupStore store,
    PeerClient client,
    PeerDirectory directory,
    ProbeHints hints,
    LocalDevice device,
    SettingsService settings,
    TimeProvider timeProvider,
    ILogger<GroupService> logger) : BackgroundService
{
    public const int MaxMembers = 100;
    public const int MaxHosted = 10;
    public const int MaxJoined = 50;
    public const int MaxNoteLength = 200;
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);

    private const int RequestsPerWindow = 5;
    private const int MaxPendingPerGroup = 50;
    private const int DeclinesBeforeLock = 3;
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeclineLock = TimeSpan.FromHours(24);
    private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeMembersFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    private readonly Lock gate = new();
    private readonly Dictionary<string, long> versions = [];
    private readonly Dictionary<string, TaskCompletionSource> signals = [];
    private readonly Dictionary<DeviceId, List<DateTimeOffset>> recentRequests = [];
    private readonly List<GroupInvitation> invitations = [];
    private readonly Dictionary<string, bool> hostReachable = [];
    private readonly Dictionary<string, Task> pollers = [];

    /// <summary>Host side: someone asked to join one of our groups.</summary>
    public event EventHandler<JoinRequest>? JoinRequested;

    public event EventHandler<GroupInvitation>? InvitationReceived;

    public event EventHandler<GroupNotice>? Notice;

    /// <summary>Invitations or Host reachability changed (groups themselves: <see cref="GroupStore.Changed"/>).</summary>
    public event EventHandler? Changed;

    public bool IsEnabled => settings.Current.GroupsEnabled;

    /// <summary>Member side: whether the Host answered the last poll; null until the first answer.</summary>
    public bool? IsHostReachable(string groupId)
    {
        lock (gate)
        {
            return hostReachable.TryGetValue(groupId, out bool reachable) ? reachable : null;
        }
    }

    public IReadOnlyList<GroupInvitation> GetInvitations()
    {
        lock (gate)
        {
            return [.. invitations];
        }
    }

    // ───────────── Host: managing our groups ─────────────

    /// <exception cref="InvalidOperationException">With a message for the user.</exception>
    public async Task<Group> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        name = ValidName(name);
        if (store.GetAll().Count(g => g.Role == GroupRole.Host) >= MaxHosted)
        {
            throw new InvalidOperationException($"Mỗi máy tạo được tối đa {MaxHosted} nhóm.");
        }

        var group = new Group(Guid.NewGuid().ToString("N"), name, device.DeviceId, GroupRole.Host, [new GroupMember(device.DeviceId, device.DisplayName, true)]);
        await store.SaveAsync(group, cancellationToken);
        logger.LogInformation("Group {Group} ({Name}) created", group.Id, name);
        return group;
    }

    public async Task RenameAsync(string groupId, string name)
    {
        Group group = Hosted(groupId);
        await store.SaveAsync(group with { Name = ValidName(name) });
        Bump(groupId);
    }

    /// <summary>Deletes the group; members learn it on their next poll (404) and forget it too.</summary>
    public async Task CloseAsync(string groupId)
    {
        Hosted(groupId);
        await store.RemoveAsync(groupId);
        Bump(groupId);
        logger.LogInformation("Group {Group} closed", groupId);
    }

    public async Task ApproveAsync(string groupId, DeviceId peer)
    {
        Group group = Hosted(groupId);
        JoinRequest request = store.GetRequest(groupId, peer) ?? throw new InvalidOperationException("Yêu cầu này không còn nữa.");
        await AddMemberAsync(group, peer, request.Name);
    }

    public async Task DeclineAsync(string groupId, DeviceId peer)
    {
        Hosted(groupId);
        if (store.GetRequest(groupId, peer) is { } request)
        {
            // The time of the decline starts the lock (docs/07-groups.md §3).
            await store.SaveRequestAsync(request with
            {
                State = JoinRequestState.Declined,
                DeclinedCount = request.DeclinedCount + 1,
                RequestedAt = timeProvider.GetUtcNow(),
            });
            Bump(groupId);
        }
    }

    public async Task RemoveMemberAsync(string groupId, DeviceId peer)
    {
        Group group = Hosted(groupId);
        if (peer == device.DeviceId)
        {
            throw new InvalidOperationException("Host không tự rời nhóm được; hãy đóng nhóm.");
        }

        await store.SaveAsync(group with { Members = [.. group.Members.Where(m => m.DeviceId != peer)] });
        await store.RemoveRequestAsync(groupId, peer);
        Bump(groupId);
    }

    /// <summary>
    /// Invites <paramref name="peer"/>: the invitation is a pre-approval, so the join request that follows when they
    /// accept is let in right away.
    /// </summary>
    /// <exception cref="PeerUnreachableException">The peer could not be reached.</exception>
    /// <exception cref="PeerApiException">The peer refused (e.g. rate limited, groups disabled there).</exception>
    public async Task InviteAsync(string groupId, DeviceId peer, string name, string host, CancellationToken cancellationToken = default)
    {
        Group group = Hosted(groupId);
        if (group.Contains(peer))
        {
            return;
        }

        if (group.Members.Count >= MaxMembers)
        {
            throw new InvalidOperationException($"Nhóm đã đủ {MaxMembers} thành viên.");
        }

        JoinRequest? previous = store.GetRequest(groupId, peer);
        await store.SaveRequestAsync(new JoinRequest(
            groupId, peer, name, host, null, JoinRequestState.Invited, previous?.DeclinedCount ?? 0, timeProvider.GetUtcNow()), cancellationToken);
        try
        {
            var body = new GroupInvitationBody(groupId, group.Name, device.DisplayName);
            using HttpResponseMessage response = await client.SendAsync(
                peer, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, "groups/invitations")) { Content = JsonContent.Create(body) },
                RequestTimeout, cancellationToken);
            await PeerClient.EnsureSuccessAsync(response, cancellationToken);
        }
        catch
        {
            if (previous is null)
            {
                await store.RemoveRequestAsync(groupId, peer, CancellationToken.None);
            }
            else
            {
                await store.SaveRequestAsync(previous, CancellationToken.None);
            }

            throw;
        }
    }

    // ───────────── Host: API handlers ─────────────

    internal async Task<JoinRequestResult> HandleJoinRequestAsync(DeviceId caller, string groupId, GroupJoinRequestBody body)
    {
        if (!IsEnabled || store.Get(groupId) is not { Role: GroupRole.Host } group)
        {
            return JoinRequestResult.NotFound;
        }

        if (body.Name is not { Length: <= PresenceCodec.MaxNameLength } || body.Host is not { Length: <= 255 } || body.Note is { Length: > MaxNoteLength })
        {
            return JoinRequestResult.Invalid;
        }

        if (group.Contains(caller))
        {
            return JoinRequestResult.Member;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        JoinRequest? existing = store.GetRequest(groupId, caller);
        if (existing is { State: JoinRequestState.Invited })
        {
            await AddMemberAsync(group, caller, body.Name.Trim());
            return JoinRequestResult.Member;
        }

        if ((existing is { State: JoinRequestState.Declined, DeclinedCount: >= DeclinesBeforeLock } && now - existing.RequestedAt < DeclineLock)
            || !TryCountRequest(caller, now)
            || (existing is not { State: JoinRequestState.Pending } && store.GetRequests(groupId).Count(r => r.State == JoinRequestState.Pending) >= MaxPendingPerGroup))
        {
            return JoinRequestResult.RateLimited;
        }

        if (group.Members.Count >= MaxMembers)
        {
            return JoinRequestResult.Full;
        }

        var request = new JoinRequest(
            groupId, caller, body.Name.Trim(), body.Host, string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim(),
            JoinRequestState.Pending, existing?.DeclinedCount ?? 0, now);
        await store.SaveRequestAsync(request);
        logger.LogInformation("{Peer} ({Name}) asks to join group {Group}", caller, request.Name, groupId);
        JoinRequested?.Invoke(this, request);
        return JoinRequestResult.Pending;
    }

    /// <summary>
    /// What <paramref name="caller"/> may know about the group: the member list for members (waiting until it changes
    /// after <paramref name="since"/>), the state of its join request otherwise. Null (404) for anyone else, which is
    /// also how removed members and members of a closed group find out.
    /// </summary>
    internal async Task<GroupStateResponse?> GetStateAsync(DeviceId caller, string groupId, long since, TimeSpan wait, CancellationToken cancellationToken)
    {
        Task signal;
        lock (gate)
        {
            signal = SignalFor(groupId).Task;
        }

        GroupStateResponse? state = BuildState(caller, groupId);
        bool unchanged = state is { Status: GroupStatus.Pending } || (state is { Status: GroupStatus.Member } && state.Version == since);
        if (!unchanged || wait <= TimeSpan.Zero)
        {
            return state;
        }

        try
        {
            await signal.WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
        }

        return BuildState(caller, groupId);
    }

    /// <summary>The caller leaves the group or withdraws its join request.</summary>
    internal async Task HandleLeaveAsync(DeviceId caller, string groupId)
    {
        if (store.Get(groupId) is not { Role: GroupRole.Host } group)
        {
            return;
        }

        if (group.Contains(caller) && caller != device.DeviceId)
        {
            await store.SaveAsync(group with { Members = [.. group.Members.Where(m => m.DeviceId != caller)] });
        }

        await store.RemoveRequestAsync(groupId, caller);
        Bump(groupId);
    }

    // ───────────── Member side ─────────────

    /// <summary>Asks the Host of <paramref name="groupId"/> to let us in. The poll loop picks up the answer.</summary>
    /// <exception cref="InvalidOperationException">With a message for the user.</exception>
    /// <exception cref="PeerUnreachableException">The Host could not be reached.</exception>
    /// <exception cref="PeerApiException">The Host refused (404 group gone, 429 too many requests or declined too often, 503 full).</exception>
    public async Task RequestJoinAsync(DeviceId hostId, string groupId, string groupName, string? note, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (store.Get(groupId) is { Role: not GroupRole.Pending })
        {
            return;
        }

        if (store.GetAll().Count(g => g.Role != GroupRole.Host) >= MaxJoined)
        {
            throw new InvalidOperationException($"Mỗi máy tham gia được tối đa {MaxJoined} nhóm.");
        }

        var body = new GroupJoinRequestBody(device.DisplayName, LocalDevice.HostName, string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        using HttpResponseMessage response = await client.SendAsync(
            hostId, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"groups/{groupId}/join-requests")) { Content = JsonContent.Create(body) },
            RequestTimeout, cancellationToken);
        await PeerClient.ReadAsync<GroupJoinResponse>(response, cancellationToken);

        // Saved as pending either way; the first poll brings the member list when the Host already let us in.
        await store.SaveAsync(new Group(groupId, ValidName(groupName, fallback: true), hostId, GroupRole.Pending, []), cancellationToken);
    }

    /// <summary>Leaves the group (or withdraws our join request). We forget it even when the Host is offline.</summary>
    public async Task LeaveAsync(string groupId)
    {
        if (store.Get(groupId) is not { Role: not GroupRole.Host } group)
        {
            return;
        }

        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                group.HostId, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"groups/{groupId}/leave")), RequestTimeout, CancellationToken.None);
        }
        catch (PeerUnreachableException)
        {
            logger.LogInformation("Left group {Group} while its Host is offline", groupId);
        }

        await store.RemoveAsync(groupId);
    }

    /// <exception cref="PeerUnreachableException">The Host could not be reached.</exception>
    /// <exception cref="PeerApiException">The Host refused.</exception>
    public async Task AcceptInvitationAsync(GroupInvitation invitation, CancellationToken cancellationToken = default)
    {
        await RequestJoinAsync(invitation.HostId, invitation.GroupId, invitation.GroupName, null, cancellationToken);
        DismissInvitation(invitation);
    }

    public void DismissInvitation(GroupInvitation invitation)
    {
        lock (gate)
        {
            invitations.RemoveAll(i => i.GroupId == invitation.GroupId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal JoinRequestResult HandleInvitation(DeviceId caller, GroupInvitationBody body)
    {
        if (!IsEnabled)
        {
            return JoinRequestResult.NotFound;
        }

        if (!PresenceCodec.IsValidGroupId(body.GroupId)
            || body.GroupName is not { Length: > 0 and <= PresenceCodec.MaxGroupNameLength }
            || body.HostName is not { Length: <= PresenceCodec.MaxNameLength })
        {
            return JoinRequestResult.Invalid;
        }

        if (store.Get(body.GroupId) is { Role: not GroupRole.Pending })
        {
            return JoinRequestResult.Member;
        }

        if (!TryCountRequest(caller, timeProvider.GetUtcNow()))
        {
            return JoinRequestResult.RateLimited;
        }

        var invitation = new GroupInvitation(body.GroupId, body.GroupName.Trim(), caller, body.HostName.Trim());
        lock (gate)
        {
            invitations.RemoveAll(i => i.GroupId == invitation.GroupId);
            invitations.Add(invitation);
        }

        logger.LogInformation("{Host} invites us into group {Group}", caller, body.GroupId);
        InvitationReceived?.Invoke(this, invitation);
        Changed?.Invoke(this, EventArgs.Empty);
        return JoinRequestResult.Pending;
    }

    /// <summary>Starts a poll loop for every group we belong to or wait for, and keeps them running.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Tick);
            do
            {
                foreach (Group group in store.GetAll().Where(g => g.Role != GroupRole.Host))
                {
                    lock (gate)
                    {
                        if (!pollers.TryGetValue(group.Id, out Task? running) || running.IsCompleted)
                        {
                            pollers[group.Id] = PollAsync(group.Id, stoppingToken);
                        }
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Task[] running;
            lock (gate)
            {
                running = [.. pollers.Values];
            }

            await Task.WhenAll(running);
        }
    }

    private async Task PollAsync(string groupId, CancellationToken stoppingToken)
    {
        await Task.Yield();
        long since = 0;
        while (!stoppingToken.IsCancellationRequested && IsEnabled && store.Get(groupId) is { Role: not GroupRole.Host } group)
        {
            try
            {
                using HttpResponseMessage response = await client.SendAsync(
                    group.HostId,
                    b => new HttpRequestMessage(HttpMethod.Get, new Uri(b, string.Create(CultureInfo.InvariantCulture, $"groups/{groupId}/state?since={since}&wait={(int)PollWait.TotalSeconds}"))),
                    PollWait + RequestTimeout,
                    stoppingToken);
                GroupStateResponse state = await PeerClient.ReadAsync<GroupStateResponse>(response, stoppingToken);
                SetReachable(groupId, true);
                switch (state.Status)
                {
                    case GroupStatus.Member:
                        await ApplyAsync(group, state);
                        since = state.Version;
                        break;
                    case GroupStatus.Declined:
                        await EndAsync(group, $"Host không đồng ý cho bạn vào nhóm \"{group.Name}\".");
                        return;
                    default:
                        // Still pending: the Host already waited for a decision before answering.
                        break;
                }
            }
            catch (PeerApiException ex) when (ex.Status is 403 or 404)
            {
                // Closed, removed, or the Host blocked us: all the same to us.
                await EndAsync(group, group.Role == GroupRole.Pending
                    ? $"Nhóm \"{group.Name}\" không còn nhận bạn vào."
                    : $"Bạn không còn ở trong nhóm \"{group.Name}\".");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is PeerUnreachableException or PeerApiException or HttpRequestException or System.Text.Json.JsonException)
            {
                logger.LogDebug(ex, "Host of group {Group} not available", groupId);
                SetReachable(groupId, false);
                try
                {
                    await Task.Delay(RetryAfter, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Takes the Host's member list; members we do not hear yet are probed, so other subnets work too.</summary>
    private async Task ApplyAsync(Group group, GroupStateResponse state)
    {
        var members = new List<GroupMember>();
        foreach (GroupMemberEntry entry in state.Members.Take(MaxMembers))
        {
            if (DeviceId.TryParse(entry.DeviceId, out DeviceId id))
            {
                members.Add(new GroupMember(id, entry.Name.Length > PresenceCodec.MaxNameLength ? entry.Name[..PresenceCodec.MaxNameLength] : entry.Name, entry.IsHost));
            }
        }

        string name = ValidName(state.Name, fallback: true);
        Group current = store.Get(group.Id) ?? group;
        if (current.Role != GroupRole.Member || current.Name != name || !current.Members.SequenceEqual(members))
        {
            await store.SaveAsync(current with { Name = name, Role = GroupRole.Member, Members = members });
            if (current.Role == GroupRole.Pending)
            {
                Notice?.Invoke(this, new GroupNotice(name, $"Bạn đã vào nhóm \"{name}\"."));
            }
        }

        HashSet<DeviceId> heard = [.. directory.Snapshot().Where(p => p is { Source: PeerSource.Direct, State: PeerState.Online }).Select(p => p.DeviceId)];
        foreach (GroupMemberEntry entry in state.Members)
        {
            if (DeviceId.TryParse(entry.DeviceId, out DeviceId id) && id != device.DeviceId && !heard.Contains(id)
                && IPAddress.TryParse(entry.Address, out IPAddress? address) && entry.DiscoveryPort is > 0 and <= 65535)
            {
                hints.Add(new IPEndPoint(address, entry.DiscoveryPort), ProbeMembersFor);
            }
        }
    }

    private async Task EndAsync(Group group, string message)
    {
        if (store.Get(group.Id) is null)
        {
            return; // we left it ourselves while this poll was waiting
        }

        await store.RemoveAsync(group.Id);
        lock (gate)
        {
            hostReachable.Remove(group.Id);
        }

        logger.LogInformation("Group {Group}: {Message}", group.Id, message);
        Notice?.Invoke(this, new GroupNotice(group.Name, message));
    }

    // ───────────── Helpers ─────────────

    private GroupStateResponse? BuildState(DeviceId caller, string groupId)
    {
        if (!IsEnabled || store.Get(groupId) is not { Role: GroupRole.Host } group)
        {
            return null;
        }

        long version = VersionOf(groupId);
        if (group.Contains(caller))
        {
            Dictionary<DeviceId, PeerInfo> online = directory.Snapshot().Where(p => p.State == PeerState.Online).ToDictionary(p => p.DeviceId);
            return new GroupStateResponse(GroupStatus.Member, version, group.Name,
            [
                .. group.Members.Select(m => online.TryGetValue(m.DeviceId, out PeerInfo? peer)
                    ? new GroupMemberEntry(m.DeviceId.Value, m.Name, m.IsHost, peer.Address.ToString(), peer.DiscoveryPort)
                    : new GroupMemberEntry(m.DeviceId.Value, m.Name, m.IsHost, null, 0)),
            ]);
        }

        return store.GetRequest(groupId, caller)?.State switch
        {
            JoinRequestState.Pending or JoinRequestState.Invited => new GroupStateResponse(GroupStatus.Pending, version, group.Name, []),
            JoinRequestState.Declined => new GroupStateResponse(GroupStatus.Declined, version, group.Name, []),
            _ => null,
        };
    }

    private async Task AddMemberAsync(Group group, DeviceId peer, string name)
    {
        if (!group.Contains(peer))
        {
            if (group.Members.Count >= MaxMembers)
            {
                throw new InvalidOperationException($"Nhóm đã đủ {MaxMembers} thành viên.");
            }

            await store.SaveAsync(group with { Members = [.. group.Members, new GroupMember(peer, name, false)] });
        }

        await store.RemoveRequestAsync(group.Id, peer);
        Bump(group.Id);
        logger.LogInformation("{Peer} ({Name}) joined group {Group}", peer, name, group.Id);
    }

    private Group Hosted(string groupId) =>
        store.Get(groupId) is { Role: GroupRole.Host } group ? group : throw new InvalidOperationException("Nhóm này không còn.");

    private void EnsureEnabled()
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Quản trị viên đã tắt tính năng nhóm.");
        }
    }

    /// <summary>1–40 characters without control characters. <paramref name="fallback"/>: fix up a name from the network instead of refusing it.</summary>
    private static string ValidName(string name, bool fallback = false)
    {
        string cleaned = new string([.. (name ?? "").Where(c => !char.IsControl(c))]).Trim();
        if (cleaned.Length is > 0 and <= PresenceCodec.MaxGroupNameLength)
        {
            return cleaned;
        }

        return fallback
            ? cleaned.Length == 0 ? "Nhóm" : cleaned[..PresenceCodec.MaxGroupNameLength]
            : throw new InvalidOperationException($"Tên nhóm cần từ 1 đến {PresenceCodec.MaxGroupNameLength} ký tự.");
    }

    private bool TryCountRequest(DeviceId caller, DateTimeOffset now)
    {
        lock (gate)
        {
            List<DateTimeOffset> recent = recentRequests.TryGetValue(caller, out List<DateTimeOffset>? list) ? list : recentRequests[caller] = [];
            recent.RemoveAll(t => now - t > RequestWindow);
            if (recent.Count >= RequestsPerWindow)
            {
                return false;
            }

            recent.Add(now);
            return true;
        }
    }

    private void SetReachable(string groupId, bool reachable)
    {
        bool changed;
        lock (gate)
        {
            changed = !hostReachable.TryGetValue(groupId, out bool previous) || previous != reachable;
            hostReachable[groupId] = reachable;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Versions start at the start time, so a member's version from before a Host restart counts as changed.</summary>
    private long VersionOf(string groupId)
    {
        lock (gate)
        {
            return versions.TryGetValue(groupId, out long version) ? version : versions[groupId] = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        }
    }

    /// <summary>The group changed: new version, and waiting polls answer now.</summary>
    private void Bump(string groupId)
    {
        lock (gate)
        {
            versions[groupId] = VersionOf(groupId) + 1;
            SignalFor(groupId).TrySetResult();
            signals.Remove(groupId);
        }
    }

    private TaskCompletionSource SignalFor(string groupId) =>
        signals.TryGetValue(groupId, out TaskCompletionSource? signal)
            ? signal
            : signals[groupId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
}
