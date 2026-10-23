using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Groups;

public enum GroupRole
{
    /// <summary>We created the group and keep its member list.</summary>
    Host,

    /// <summary>The Host approved us; the member list is a cached copy of the Host's.</summary>
    Member,

    /// <summary>We asked to join and wait for the Host.</summary>
    Pending,
}

public sealed record GroupMember(DeviceId DeviceId, string Name, bool IsHost);

/// <summary>A group as it was and as it is now; <paramref name="After"/> is null when we no longer have it.</summary>
public sealed record GroupChange(Group? Before, Group? After);

/// <param name="HostOnlyChat">"Chỉ nhắn khi Host online": set by the Host, carried in its signed list (docs/16-group-chat.md §3.7).</param>
public sealed record Group(string Id, string Name, DeviceId HostId, GroupRole Role, IReadOnlyList<GroupMember> Members, bool HostOnlyChat = false)
{
    public bool Contains(DeviceId id) => Members.Any(m => m.DeviceId == id);
}

public enum JoinRequestState
{
    Pending,

    /// <summary>The Host invited the peer: its join request is approved as soon as it arrives.</summary>
    Invited,

    Declined,
}

/// <summary>Host side: someone asked to join one of our groups, or we invited them.</summary>
public sealed record JoinRequest(
    string GroupId, DeviceId PeerId, string Name, string Host, string? Note, JoinRequestState State, int DeclinedCount, DateTimeOffset RequestedAt);

/// <summary>
/// Groups we host or belong to, their members, and join requests (docs/07-groups.md, docs/09-data-storage.md).
/// Held in memory because every offer and download checks membership. Network work lives in GroupService.
/// </summary>
public sealed class GroupStore(SqliteDatabase database, TrustStore trust, SettingsService settings, TimeProvider timeProvider)
{
    /// <summary>"Chỉ nhắn khi Host online": how recent the Host's last answer must be. It answers at least every 20 s.</summary>
    public static readonly TimeSpan HostFresh = TimeSpan.FromMinutes(1);

    /// <summary>How long someone gone from the list without a time is remembered: as long as the Host names who left.</summary>
    private static readonly TimeSpan DepartureKept = TimeSpan.FromDays(31);

    /// <summary>Changes of the Host's clock smaller than this are noise of the network, not worth a write.</summary>
    private const long HostOffsetStep = 100;

    /// <summary>When we last heard the Host is written at most this often; the moment itself is kept in memory.</summary>
    private const long SyncedWriteStep = 60_000;

    private readonly Lock gate = new();
    private Dictionary<string, Group> groups = [];
    private Dictionary<(string GroupId, DeviceId PeerId), JoinRequest> requests = [];
    private Dictionary<string, GroupRoster> rosters = [];
    private Dictionary<string, long> hostOffsets = [];
    private Dictionary<string, long> synced = [];
    private Dictionary<string, long> syncedWritten = [];
    private readonly HashSet<string> hostLost = [];
    private Dictionary<string, Dictionary<DeviceId, long>> departures = [];

    /// <summary>Raised after any change to groups, members or join requests.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after a group is saved or forgotten (the audit log).</summary>
    public event EventHandler<GroupChange>? GroupChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var groupRows = await connection.QueryAsync<GroupRow>("SELECT GroupId, Name, HostDeviceId, IsHostedByMe, State, HostOnlyChat FROM Groups;");
        var memberRows = (await connection.QueryAsync<MemberRow>(
            "SELECT GroupId, DeviceId, DisplayName, IsHost FROM GroupMembers ORDER BY JoinedAt;")).ToLookup(m => m.GroupId);
        var requestRows = await connection.QueryAsync<RequestRow>(
            "SELECT GroupId, DeviceId, DisplayName, HostName, Note, State, DeclinedCount, RequestedAt FROM GroupJoinRequests;");
        var rosterRows = await connection.QueryAsync<string>("SELECT Roster FROM GroupRosters;");
        var offsetRows = (await connection.QueryAsync<(string GroupId, long HostOffsetMs, long SyncedAt)>(
            "SELECT GroupId, HostOffsetMs, SyncedAt FROM Groups;")).ToList();
        var departureRows = await connection.QueryAsync<(string GroupId, string DeviceId, long ConfirmedAt)>(
            "SELECT GroupId, DeviceId, ConfirmedAt FROM GroupDepartures;");

        lock (gate)
        {
            groups = groupRows.Select(g => g.ToGroup(memberRows[g.GroupId])).ToDictionary(g => g.Id);
            requests = requestRows.Select(r => r.ToRequest()).ToDictionary(r => (r.GroupId, r.PeerId));
            rosters = rosterRows.Select(r => JsonSerializer.Deserialize<GroupRoster>(r, PeerJson.Options)!).ToDictionary(r => r.GroupId);
            hostOffsets = offsetRows.ToDictionary(r => r.GroupId, r => r.HostOffsetMs);
            synced = offsetRows.ToDictionary(r => r.GroupId, r => r.SyncedAt);
            syncedWritten = new Dictionary<string, long>(synced);
            departures = departureRows.GroupBy(r => r.GroupId)
                .ToDictionary(d => d.Key, d => d.ToDictionary(r => DeviceId.Parse(r.DeviceId), r => r.ConfirmedAt));
        }
    }

    public IReadOnlyList<Group> GetAll()
    {
        lock (gate)
        {
            return [.. groups.Values.OrderBy(g => g.Role).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    public Group? Get(string groupId)
    {
        lock (gate)
        {
            return groups.GetValueOrDefault(groupId);
        }
    }

    /// <summary>
    /// Both <paramref name="peer"/> and we are in <paramref name="groupId"/> (as Host or approved member),
    /// as far as our copy of the member list says. Never while IT has turned groups off.
    /// </summary>
    public bool IsMember(string groupId, DeviceId peer)
    {
        lock (gate)
        {
            return settings.Current.GroupsEnabled
                && groups.TryGetValue(groupId, out Group? group) && group.Role != GroupRole.Pending && group.Contains(peer);
        }
    }

    public bool SharesGroup(DeviceId peer)
    {
        lock (gate)
        {
            return settings.Current.GroupsEnabled && groups.Values.Any(g => g.Role != GroupRole.Pending && g.Contains(peer));
        }
    }

    /// <summary>Who may offer us files and download ours: contacts, or members of the group the offer belongs to.</summary>
    public bool CanExchangeFiles(DeviceId peer, string? groupId) =>
        trust.GetLevel(peer) == TrustLevel.Trusted || (groupId is not null && IsMember(groupId, peer));

    /// <summary>The newest member list signed by the Host of <paramref name="groupId"/> we have (docs/07-groups.md §4.1).</summary>
    public GroupRoster? GetRoster(string groupId)
    {
        lock (gate)
        {
            return rosters.GetValueOrDefault(groupId);
        }
    }

    /// <summary>
    /// Whether group messages may come and go now: always, unless the group is "Chỉ nhắn khi Host online" and we have
    /// not heard from its Host within <see cref="HostFresh"/>, or the last try failed. The Host itself always may.
    /// </summary>
    public bool CanChat(string groupId)
    {
        lock (gate)
        {
            return settings.Current.GroupsEnabled && groups.TryGetValue(groupId, out Group? group) && group.Role != GroupRole.Pending
                && (group.Role == GroupRole.Host || !group.HostOnlyChat
                    || (!hostLost.Contains(groupId) && timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - synced.GetValueOrDefault(groupId) <= (long)HostFresh.TotalMilliseconds));
        }
    }

    /// <summary>
    /// The Host just answered with its member list: what our list says is confirmed as of now. Call after taking
    /// the answer, so who left in it was last confirmed at the time before.
    /// </summary>
    public async Task MarkSyncedAsync(string groupId)
    {
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        bool write;
        bool flipped;
        lock (gate)
        {
            if (!groups.TryGetValue(groupId, out Group? group))
            {
                return;
            }

            flipped = hostLost.Remove(groupId) && group.HostOnlyChat;
            synced[groupId] = now;
            write = now - syncedWritten.GetValueOrDefault(groupId) >= SyncedWriteStep;
            if (write)
            {
                syncedWritten[groupId] = now;
            }
        }

        if (write)
        {
            await using SqliteConnection connection = await database.OpenAsync();
            await connection.ExecuteAsync("UPDATE Groups SET SyncedAt = @now WHERE GroupId = @groupId;", new { groupId, now });
        }

        if (flipped)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A try at the Host failed: a "Chỉ nhắn khi Host online" group stops until it answers again.</summary>
    public void MarkHostLost(string groupId)
    {
        bool flipped;
        lock (gate)
        {
            flipped = hostLost.Add(groupId) && groups.TryGetValue(groupId, out Group? group) && group.HostOnlyChat;
        }

        if (flipped)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Who is gone from our list without a time from the Host, and when we last heard they were a member (ms, our clock).</summary>
    public IReadOnlyDictionary<DeviceId, long> GetDepartures(string groupId)
    {
        lock (gate)
        {
            return departures.TryGetValue(groupId, out var gone) ? new Dictionary<DeviceId, long>(gone) : [];
        }
    }

    /// <summary>How far the Host's clock is ahead of ours, as last measured; zero before that (or on the Host).</summary>
    public TimeSpan GetHostOffset(string groupId)
    {
        lock (gate)
        {
            return TimeSpan.FromMilliseconds(hostOffsets.GetValueOrDefault(groupId));
        }
    }

    /// <summary>Keeps a fresh measure of the Host's clock; written only when it moved, not on every answer.</summary>
    public async Task SetHostOffsetAsync(string groupId, TimeSpan offset)
    {
        long ms = (long)offset.TotalMilliseconds;
        lock (gate)
        {
            if (!groups.ContainsKey(groupId) || Math.Abs(hostOffsets.GetValueOrDefault(groupId) - ms) < HostOffsetStep)
            {
                return;
            }

            hostOffsets[groupId] = ms;
        }

        await using SqliteConnection connection = await database.OpenAsync();
        await connection.ExecuteAsync("UPDATE Groups SET HostOffsetMs = @ms WHERE GroupId = @groupId;", new { groupId, ms });
    }

    public IReadOnlyList<JoinRequest> GetRequests(string groupId)
    {
        lock (gate)
        {
            return [.. requests.Values.Where(r => r.GroupId == groupId).OrderBy(r => r.RequestedAt)];
        }
    }

    public JoinRequest? GetRequest(string groupId, DeviceId peer)
    {
        lock (gate)
        {
            return requests.GetValueOrDefault((groupId, peer));
        }
    }

    // ───────────── Changes ─────────────

    /// <summary>Adds the group or replaces it (name, role, members), e.g. with a fresh copy from the Host.</summary>
    public async Task SaveAsync(Group group, CancellationToken cancellationToken = default)
    {
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // On a member: who is gone, and when we last heard they were in (unknown before the first answer).
        Dictionary<DeviceId, long> gone;
        Group? before;
        lock (gate)
        {
            gone = departures.TryGetValue(group.Id, out var known) ? new Dictionary<DeviceId, long>(known) : [];
            before = groups.GetValueOrDefault(group.Id);
            if (group.Role == GroupRole.Member && before is { Role: GroupRole.Member }
                && synced.GetValueOrDefault(group.Id) is > 0 and long confirmed)
            {
                foreach (GroupMember left in before.Members.Where(m => !group.Contains(m.DeviceId)))
                {
                    gone[left.DeviceId] = confirmed;
                }
            }

            foreach (DeviceId back in gone.Keys.Where(group.Contains).ToList())
            {
                gone.Remove(back);
            }

            foreach (DeviceId old in gone.Where(d => now - d.Value > (long)DepartureKept.TotalMilliseconds).Select(d => d.Key).ToList())
            {
                gone.Remove(old);
            }
        }

        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(
            """
            INSERT INTO Groups (GroupId, Name, HostDeviceId, IsHostedByMe, CreatedAt, JoinedAt, State, HostOnlyChat)
            VALUES (@Id, @Name, @HostId, @IsHost, @now, @now, @State, @HostOnlyChat)
            ON CONFLICT (GroupId) DO UPDATE SET Name = excluded.Name, State = excluded.State, HostOnlyChat = excluded.HostOnlyChat;
            """,
            new { group.Id, group.Name, HostId = group.HostId.Value, IsHost = group.Role == GroupRole.Host, now, State = StateName(group.Role), group.HostOnlyChat },
            transaction);
        await connection.ExecuteAsync("DELETE FROM GroupDepartures WHERE GroupId = @Id;", new { group.Id }, transaction);
        foreach ((DeviceId peer, long confirmedAt) in gone)
        {
            await connection.ExecuteAsync(
                "INSERT INTO GroupDepartures (GroupId, DeviceId, ConfirmedAt) VALUES (@GroupId, @DeviceId, @confirmedAt);",
                new { GroupId = group.Id, DeviceId = peer.Value, confirmedAt },
                transaction);
        }

        await connection.ExecuteAsync("DELETE FROM GroupMembers WHERE GroupId = @Id;", new { group.Id }, transaction);
        foreach (GroupMember member in group.Members)
        {
            await connection.ExecuteAsync(
                "INSERT INTO GroupMembers (GroupId, DeviceId, DisplayName, IsHost, JoinedAt) VALUES (@GroupId, @DeviceId, @Name, @IsHost, @now);",
                new { GroupId = group.Id, DeviceId = member.DeviceId.Value, member.Name, member.IsHost, now },
                transaction);
        }

        await transaction.CommitAsync(cancellationToken);
        lock (gate)
        {
            groups[group.Id] = group;
            departures[group.Id] = gone;
        }

        GroupChanged?.Invoke(this, new GroupChange(before, group));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Keeps <paramref name="roster"/> (checked by the caller) as the group's newest. False, keeping ours, when it is not
    /// newer than ours.
    /// </summary>
    public async Task<bool> TrySaveRosterAsync(GroupRoster roster, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (rosters.TryGetValue(roster.GroupId, out GroupRoster? ours) && ours.Version >= roster.Version)
            {
                return false;
            }

            rosters[roster.GroupId] = roster;
        }

        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO GroupRosters (GroupId, Version, Roster) VALUES (@GroupId, @Version, @json)
            ON CONFLICT (GroupId) DO UPDATE SET Version = excluded.Version, Roster = excluded.Roster WHERE excluded.Version > GroupRosters.Version;
            """,
            new { roster.GroupId, roster.Version, json = JsonSerializer.Serialize(roster, PeerJson.Options) });
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Forgets the group: closed, left, or we were removed. Its requests go with it.</summary>
    public async Task RemoveAsync(string groupId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            DELETE FROM GroupJoinRequests WHERE GroupId = @groupId;
            DELETE FROM GroupMembers WHERE GroupId = @groupId;
            DELETE FROM GroupRosters WHERE GroupId = @groupId;
            DELETE FROM GroupDepartures WHERE GroupId = @groupId;
            DELETE FROM Groups WHERE GroupId = @groupId;
            """,
            new { groupId });
        Group? before;
        lock (gate)
        {
            groups.Remove(groupId, out before);
            rosters.Remove(groupId);
            hostOffsets.Remove(groupId);
            synced.Remove(groupId);
            syncedWritten.Remove(groupId);
            hostLost.Remove(groupId);
            departures.Remove(groupId);
            foreach (var key in requests.Keys.Where(k => k.GroupId == groupId).ToList())
            {
                requests.Remove(key);
            }
        }

        if (before is not null)
        {
            GroupChanged?.Invoke(this, new GroupChange(before, null));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveRequestAsync(JoinRequest request, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO GroupJoinRequests (GroupId, DeviceId, DisplayName, HostName, Note, State, DeclinedCount, RequestedAt)
            VALUES (@GroupId, @PeerId, @Name, @Host, @Note, @State, @DeclinedCount, @RequestedAt)
            ON CONFLICT (GroupId, DeviceId) DO UPDATE SET DisplayName = excluded.DisplayName, HostName = excluded.HostName,
              Note = excluded.Note, State = excluded.State, DeclinedCount = excluded.DeclinedCount, RequestedAt = excluded.RequestedAt;
            """,
            new
            {
                request.GroupId,
                PeerId = request.PeerId.Value,
                request.Name,
                request.Host,
                request.Note,
                State = request.State switch
                {
                    JoinRequestState.Pending => "pending",
                    JoinRequestState.Invited => "invited",
                    _ => "declined",
                },
                request.DeclinedCount,
                RequestedAt = request.RequestedAt.ToUnixTimeMilliseconds(),
            });
        lock (gate)
        {
            requests[(request.GroupId, request.PeerId)] = request;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveRequestAsync(string groupId, DeviceId peer, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM GroupJoinRequests WHERE GroupId = @groupId AND DeviceId = @peer;", new { groupId, peer = peer.Value });
        bool removed;
        lock (gate)
        {
            removed = requests.Remove((groupId, peer));
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string StateName(GroupRole role) => role == GroupRole.Pending ? "pending" : "member";

    private sealed class GroupRow
    {
        public string GroupId { get; init; } = "";

        public string Name { get; init; } = "";

        public string HostDeviceId { get; init; } = "";

        public long IsHostedByMe { get; init; }

        public string State { get; init; } = "";

        public long HostOnlyChat { get; init; }

        public Group ToGroup(IEnumerable<MemberRow> members) => new(
            GroupId,
            Name,
            DeviceId.Parse(HostDeviceId),
            IsHostedByMe != 0 ? GroupRole.Host : State == "pending" ? GroupRole.Pending : GroupRole.Member,
            [.. members.Select(m => new GroupMember(DeviceId.Parse(m.DeviceId), m.DisplayName ?? "", m.IsHost != 0))],
            HostOnlyChat != 0);
    }

    private sealed class MemberRow
    {
        public string GroupId { get; init; } = "";

        public string DeviceId { get; init; } = "";

        public string? DisplayName { get; init; }

        public long IsHost { get; init; }
    }

    private sealed class RequestRow
    {
        public string GroupId { get; init; } = "";

        public string DeviceId { get; init; } = "";

        public string? DisplayName { get; init; }

        public string? HostName { get; init; }

        public string? Note { get; init; }

        public string State { get; init; } = "";

        public long DeclinedCount { get; init; }

        public long RequestedAt { get; init; }

        public JoinRequest ToRequest() => new(
            GroupId,
            Core.Identity.DeviceId.Parse(DeviceId),
            DisplayName ?? "",
            HostName ?? "",
            Note,
            Enum.Parse<JoinRequestState>(State, ignoreCase: true),
            (int)DeclinedCount,
            DateTimeOffset.FromUnixTimeMilliseconds(RequestedAt));
    }
}
