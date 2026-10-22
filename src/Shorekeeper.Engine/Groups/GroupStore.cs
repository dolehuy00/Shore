using Dapper;
using Microsoft.Data.Sqlite;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
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

public sealed record Group(string Id, string Name, DeviceId HostId, GroupRole Role, IReadOnlyList<GroupMember> Members)
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
    private readonly Lock gate = new();
    private Dictionary<string, Group> groups = [];
    private Dictionary<(string GroupId, DeviceId PeerId), JoinRequest> requests = [];

    /// <summary>Raised after any change to groups, members or join requests.</summary>
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var groupRows = await connection.QueryAsync<GroupRow>("SELECT GroupId, Name, HostDeviceId, IsHostedByMe, State FROM Groups;");
        var memberRows = (await connection.QueryAsync<MemberRow>(
            "SELECT GroupId, DeviceId, DisplayName, IsHost FROM GroupMembers ORDER BY JoinedAt;")).ToLookup(m => m.GroupId);
        var requestRows = await connection.QueryAsync<RequestRow>(
            "SELECT GroupId, DeviceId, DisplayName, HostName, Note, State, DeclinedCount, RequestedAt FROM GroupJoinRequests;");

        lock (gate)
        {
            groups = groupRows.Select(g => g.ToGroup(memberRows[g.GroupId])).ToDictionary(g => g.Id);
            requests = requestRows.Select(r => r.ToRequest()).ToDictionary(r => (r.GroupId, r.PeerId));
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
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(
            """
            INSERT INTO Groups (GroupId, Name, HostDeviceId, IsHostedByMe, CreatedAt, JoinedAt, State)
            VALUES (@Id, @Name, @HostId, @IsHost, @now, @now, @State)
            ON CONFLICT (GroupId) DO UPDATE SET Name = excluded.Name, State = excluded.State;
            """,
            new { group.Id, group.Name, HostId = group.HostId.Value, IsHost = group.Role == GroupRole.Host, now, State = StateName(group.Role) },
            transaction);
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
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the group: closed, left, or we were removed. Its requests go with it.</summary>
    public async Task RemoveAsync(string groupId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            DELETE FROM GroupJoinRequests WHERE GroupId = @groupId;
            DELETE FROM GroupMembers WHERE GroupId = @groupId;
            DELETE FROM Groups WHERE GroupId = @groupId;
            """,
            new { groupId });
        lock (gate)
        {
            groups.Remove(groupId);
            foreach (var key in requests.Keys.Where(k => k.GroupId == groupId).ToList())
            {
                requests.Remove(key);
            }
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

        public Group ToGroup(IEnumerable<MemberRow> members) => new(
            GroupId,
            Name,
            DeviceId.Parse(HostDeviceId),
            IsHostedByMe != 0 ? GroupRole.Host : State == "pending" ? GroupRole.Pending : GroupRole.Member,
            [.. members.Select(m => new GroupMember(DeviceId.Parse(m.DeviceId), m.DisplayName ?? "", m.IsHost != 0))]);
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
