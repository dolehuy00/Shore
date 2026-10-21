using System.Text.Json.Serialization;

namespace Shorekeeper.Core.Discovery;

/// <summary>
/// UDP presence packet (docs/05-protocol.md §2). Every packet carries the full presence,
/// so a single heartbeat is enough to learn about a peer.
/// </summary>
public sealed record PresencePacket
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("host")]
    public string Host { get; init; } = "";

    [JsonPropertyName("os")]
    public string Os { get; init; } = "";

    [JsonPropertyName("app")]
    public string App { get; init; } = "";

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("addrs")]
    public IReadOnlyList<string> Addresses { get; init; } = [];

    [JsonPropertyName("status")]
    public string Status { get; init; } = PresenceStatus.Available;

    /// <summary>Roles this device offers to others, see <see cref="PresenceCapabilities"/>.</summary>
    [JsonPropertyName("caps")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Groups this device hosts, so others can ask to join (docs/07-groups.md §2). At most <see cref="MaxGroups"/>.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<PresenceGroup> Groups { get; init; } = [];

    public const int MaxGroups = 5;

    /// <summary>Department from the company directory (AD), when known. A claim, like the name.</summary>
    [JsonPropertyName("dept")]
    public string? Department { get; init; }

    /// <summary>Set when the device has a picture at <c>GET /hello/avatar</c>; changes with the picture (8 hex digits).</summary>
    [JsonPropertyName("av")]
    public string? AvatarTag { get; init; }
}

/// <param name="Members">Number of members, host included.</param>
public sealed record PresenceGroup(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("n")] int Members);

public static class PresencePacketTypes
{
    public const string Announce = "announce";
    public const string Heartbeat = "heartbeat";
    public const string Reply = "reply";
    public const string Bye = "bye";

    /// <summary>Unicast to a known address outside the multicast reach; carries our presence and asks for a reply.</summary>
    public const string Probe = "probe";

    public static bool IsKnown(string type) => type is Announce or Heartbeat or Reply or Bye or Probe;
}

public static class PresenceCapabilities
{
    /// <summary>The device relays presence for other subnets (docs/08-host-roles.md §4).</summary>
    public const string Bridge = "bridge";

    /// <summary>"Phát âm thanh" is on: contacts may listen (docs/05-protocol.md §4.7).</summary>
    public const string Audio = "audio";

    /// <summary>With <see cref="Audio"/>: listening needs the source's password.</summary>
    public const string AudioLocked = "audio-locked";

    /// <summary>Takes messages (docs/15-chat.md §2); versions before it answer them like an unknown route.</summary>
    public const string Chat = "chat";
}

public static class PresenceStatus
{
    public const string Available = "available";
    public const string Busy = "busy";
}
