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
}

public static class PresencePacketTypes
{
    public const string Announce = "announce";
    public const string Heartbeat = "heartbeat";
    public const string Reply = "reply";
    public const string Bye = "bye";

    public static bool IsKnown(string type) => type is Announce or Heartbeat or Reply or Bye;
}

public static class PresenceStatus
{
    public const string Available = "available";
    public const string Busy = "busy";
}
