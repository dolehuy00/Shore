namespace Shorekeeper.Engine.Api;

/// <summary>Response of <c>GET /api/v1/hello</c>; only what presence already makes public.</summary>
public sealed record HelloResponse(string DeviceId, string Name, string Host, string Os, string App, int[] Proto, int Port);

/// <summary>Body of <c>POST /api/v1/pairing/request</c>. Name and host are claims, shown next to the code.</summary>
public sealed record PairingRequestBody(string PairingId, string Nonce, string Name, string Host, int ApiPort, string? Note);

/// <summary>Responder's nonce plus its name, so the requester can show who it is waiting for.</summary>
public sealed record PairingRequestAccepted(string Nonce, string Name, string Host);

public sealed record PairingDecisionResponse(string Status);

public static class PairingStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
}

/// <summary>One entry of an offer. Directories end with '/' and have size 0 (docs/05-protocol.md §4.2).</summary>
public sealed record OfferFileEntry(string FileId, string RelativePath, long Size, DateTimeOffset? ModifiedAt);

/// <summary>Body of <c>POST /api/v1/inbox/offers</c>: what the sender offers; no data yet.</summary>
/// <param name="GroupId">Set when sent to fellow group members rather than contacts (docs/04-identity-security.md §4.2).</param>
public sealed record OfferManifest(
    string OfferId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? Note, long TotalSize, IReadOnlyList<OfferFileEntry> Files,
    string? GroupId = null);

public sealed record FileHashResponse(string Sha256);

/// <summary>Body of <c>POST /api/v1/offers/{offerId}/receipts</c>, sent by a recipient.</summary>
public sealed record OfferReceipt(string Status);

public static class ReceiptStatus
{
    public const string Downloading = "downloading";
    public const string Completed = "completed";

    /// <summary>The recipient took only some files; the offer stays open for the rest.</summary>
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Declined = "declined";
}

/// <summary>Response of <c>GET /api/v1/peers/known</c> (PEX): peers the answering device hears itself, and Bridges it knows.</summary>
public sealed record KnownPeersResponse(IReadOnlyList<KnownPeer> Peers, IReadOnlyList<KnownBridge> Bridges);

/// <param name="DiscoveryPort">UDP port to probe the peer on.</param>
public sealed record KnownPeer(string DeviceId, string Name, string Address, int DiscoveryPort);

/// <param name="Port">HTTPS API port of the Bridge.</param>
public sealed record KnownBridge(string DeviceId, string Address, int Port);

/// <summary>Body of <c>POST /api/v1/bridge/register</c>: the caller's public presence. The DeviceId comes from its TLS certificate.</summary>
public sealed record BridgeRegistration(string Name, string Host, string Os, string App, string Status, int Port);

/// <param name="ObservedAddr">Address the Bridge sees the caller at; this is what other peers will use.</param>
public sealed record BridgeRegistered(int LeaseSeconds, string ObservedAddr);

/// <summary>
/// Response of <c>GET /api/v1/bridge/peers?since=</c>. <paramref name="Full"/>: <paramref name="Peers"/> is the whole
/// registry; otherwise only what changed after <c>since</c>, plus DeviceIds that left.
/// </summary>
public sealed record BridgePeersResponse(long Version, bool Full, IReadOnlyList<BridgePeer> Peers, IReadOnlyList<string> Removed);

/// <param name="Port">HTTPS API port.</param>
public sealed record BridgePeer(string DeviceId, string Name, string Host, string Os, string App, string Status, string Address, int Port);

/// <summary>Body of <c>POST /api/v1/groups/{groupId}/join-requests</c>, sent to the Host. Name and host are shown to the Host.</summary>
public sealed record GroupJoinRequestBody(string Name, string Host, string? Note);

/// <summary>Response of a join request: <see cref="GroupStatus.Pending"/> or, when the Host had invited us, <see cref="GroupStatus.Member"/>.</summary>
public sealed record GroupJoinResponse(string Status);

/// <summary>
/// Response of <c>GET /api/v1/groups/{groupId}/state?since=&amp;wait=</c>, long-polled by members and by peers waiting to be approved.
/// Members get the member list; addresses are where the Host sees each member right now (empty when offline).
/// </summary>
public sealed record GroupStateResponse(string Status, long Version, string Name, IReadOnlyList<GroupMemberEntry> Members);

public sealed record GroupMemberEntry(string DeviceId, string Name, bool IsHost, string? Address, int DiscoveryPort);

public static class GroupStatus
{
    public const string Member = "member";
    public const string Pending = "pending";
    public const string Declined = "declined";
}

/// <summary>Body of <c>POST /api/v1/groups/invitations</c>: a Host invites the callee into one of its groups.</summary>
public sealed record GroupInvitationBody(string GroupId, string GroupName, string HostName);

/// <summary>Stable error codes in problem+json responses (docs/05-protocol.md §3).</summary>
public static class ApiErrorCodes
{
    public const string NotTrusted = "not_trusted";
    public const string RateLimited = "rate_limited";
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string NotRecipient = "not_recipient";
    public const string OfferClosed = "offer_closed";
    public const string SourceChanged = "source_changed";
    public const string Busy = "busy";
}
