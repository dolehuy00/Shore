using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Monitoring;

/// <summary>Event ids of the audit log, the same on every computer (docs/11-windows-deployment.md §9).</summary>
public enum AuditEvent
{
    OfferSent = 1001,
    FileSent = 1002,
    OfferReceived = 2001,
    FileReceived = 2002,
    OfferDeclined = 2003,
    ContactAdded = 3001,
    ContactBlocked = 3002,
    ContactRemoved = 3003,
    GroupJoined = 4001,
    GroupLeft = 4002,
    GroupMemberAdded = 4003,
    GroupMemberRemoved = 4004,
}

/// <summary>
/// Who sent which file to whom, for the security team, when IT turns it on (policy <c>AuditLog</c>): what was sent
/// and received, with sizes and SHA-256, and who became a contact or a fellow member. Never what was written in a
/// message (docs/11-windows-deployment.md §9).
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IAuditSink sink;
    private readonly IPolicyProvider policy;
    private readonly TrustStore trust;
    private readonly GroupStore groups;
    private readonly PeerDirectory directory;
    private readonly TimeProvider timeProvider;

    public AuditLog(IAuditSink sink, IPolicyProvider policy, TrustStore trust, GroupStore groups, PeerDirectory directory, TimeProvider timeProvider)
    {
        this.sink = sink;
        this.policy = policy;
        this.trust = trust;
        this.groups = groups;
        this.directory = directory;
        this.timeProvider = timeProvider;
        trust.LevelChanged += (_, change) => OnTrustChanged(change);
        groups.GroupChanged += (_, change) => OnGroupChanged(change);
    }

    /// <summary>Read on every write, so a policy refresh applies at once.</summary>
    public bool IsOn => policy.GetBoolean(PolicyNames.AuditLog) == true;

    /// <summary>This device, once the engine knows it.</summary>
    internal DeviceId? Self { get; set; }

    /// <param name="details">Built only when the log is on.</param>
    internal void Write(AuditEvent what, Func<JsonObject> details)
    {
        if (!IsOn)
        {
            return;
        }

        var entry = new JsonObject
        {
            ["event"] = what.ToString(),
            ["at"] = timeProvider.GetUtcNow().ToString("O"),
            ["user"] = $@"{Environment.UserDomainName}\{Environment.UserName}",
            ["device"] = Self?.Value,
        };
        JsonObject fields = details();
        foreach ((string key, JsonNode? value) in fields.ToList())
        {
            fields.Remove(key);
            entry[key] = value;
        }

        sink.Write((int)what, entry.ToJsonString(JsonOptions));
    }

    /// <summary>A device with the name we know it by: contact, fellow member, or what it announces.</summary>
    internal JsonObject Peer(DeviceId id, string? name = null) => new()
    {
        ["id"] = id.Value,
        ["name"] = name
            ?? trust.Get(id)?.ShownName
            ?? groups.GetAll().SelectMany(g => g.Members).FirstOrDefault(m => m.DeviceId == id)?.Name
            ?? directory.Snapshot().FirstOrDefault(p => p.DeviceId == id)?.DisplayName,
    };

    internal JsonObject? GroupInfo(string? groupId) =>
        groupId is null ? null : new JsonObject { ["id"] = groupId, ["name"] = groups.Get(groupId)?.Name };

    private void OnTrustChanged(TrustChange change)
    {
        if (change.After is { } after)
        {
            Write(after.TrustLevel == TrustLevel.Blocked ? AuditEvent.ContactBlocked : AuditEvent.ContactAdded, () => new JsonObject
            {
                ["peer"] = Peer(after.DeviceId, after.DisplayName),
                ["host"] = after.HostName,
            });
        }
        else if (change.Before is { } before)
        {
            Write(AuditEvent.ContactRemoved, () => new JsonObject
            {
                ["peer"] = Peer(before.DeviceId, before.DisplayName),
                ["was"] = before.TrustLevel == TrustLevel.Blocked ? "blocked" : "contact",
            });
        }
    }

    /// <summary>Asking to join is not joining: a group counts from when we host it or the Host let us in.</summary>
    private void OnGroupChanged(GroupChange change)
    {
        Group? before = change.Before is { Role: not GroupRole.Pending } b ? b : null;
        Group? after = change.After is { Role: not GroupRole.Pending } a ? a : null;
        if (after is null)
        {
            if (before is not null)
            {
                Write(AuditEvent.GroupLeft, () => new JsonObject { ["group"] = Named(before) });
            }

            return;
        }

        if (before is null)
        {
            GroupMember? host = after.Members.FirstOrDefault(m => m.IsHost);
            Write(AuditEvent.GroupJoined, () => new JsonObject
            {
                ["group"] = Named(after),
                ["role"] = after.Role == GroupRole.Host ? "host" : "member",
                ["host"] = Peer(after.HostId, host?.Name),
                ["members"] = after.Members.Count,
            });
            return;
        }

        foreach (GroupMember added in after.Members.Where(m => !before.Contains(m.DeviceId)))
        {
            Write(AuditEvent.GroupMemberAdded, () => new JsonObject { ["group"] = Named(after), ["member"] = Peer(added.DeviceId, added.Name) });
        }

        foreach (GroupMember removed in before.Members.Where(m => !after.Contains(m.DeviceId)))
        {
            Write(AuditEvent.GroupMemberRemoved, () => new JsonObject { ["group"] = Named(after), ["member"] = Peer(removed.DeviceId, removed.Name) });
        }

        static JsonObject Named(Group group) => new() { ["id"] = group.Id, ["name"] = group.Name };
    }
}
