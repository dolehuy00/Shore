namespace Shorekeeper.Engine.Api;

/// <summary>Response of <c>GET /api/v1/hello</c>; only what presence already makes public.</summary>
/// <param name="Dept">Department from the company directory; <paramref name="Avatar"/> as in presence.</param>
public sealed record HelloResponse(string DeviceId, string Name, string Host, string Os, string App, int[] Proto, int Port, string? Dept = null, string? Avatar = null);

/// <summary>Body of <c>POST /api/v1/pairing/request</c>. Name and host are claims, shown next to the code.</summary>
/// <param name="Invite">From the responder's QR code ("Kết nối bằng mã QR"): accepted without asking while it is valid.</param>
public sealed record PairingRequestBody(string PairingId, string Nonce, string Name, string Host, int ApiPort, string? Note, string? Invite = null);

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
public sealed record BridgeRegistration(string Name, string Host, string Os, string App, string Status, int Port, string? Dept = null, string? Avatar = null);

/// <param name="ObservedAddr">Address the Bridge sees the caller at; this is what other peers will use.</param>
public sealed record BridgeRegistered(int LeaseSeconds, string ObservedAddr);

/// <summary>
/// Response of <c>GET /api/v1/bridge/peers?since=</c>. <paramref name="Full"/>: <paramref name="Peers"/> is the whole
/// registry; otherwise only what changed after <c>since</c>, plus DeviceIds that left.
/// </summary>
public sealed record BridgePeersResponse(long Version, bool Full, IReadOnlyList<BridgePeer> Peers, IReadOnlyList<string> Removed);

/// <param name="Port">HTTPS API port.</param>
public sealed record BridgePeer(string DeviceId, string Name, string Host, string Os, string App, string Status, string Address, int Port, string? Dept = null, string? Avatar = null);

/// <summary>Body of <c>POST /api/v1/groups/{groupId}/join-requests</c>, sent to the Host. Name and host are shown to the Host.</summary>
public sealed record GroupJoinRequestBody(string Name, string Host, string? Note);

/// <summary>Response of a join request: <see cref="GroupStatus.Pending"/> or, when the Host had invited us, <see cref="GroupStatus.Member"/>.</summary>
public sealed record GroupJoinResponse(string Status);

/// <summary>
/// Response of <c>GET /api/v1/groups/{groupId}/state?since=&amp;wait=</c>, long-polled by members and by peers waiting to be approved.
/// Members get the member list; addresses are where the Host sees each member right now (empty when offline).
/// <see cref="Roster"/>: the same list as the Host signed it, for members to pass on; <see cref="Now"/>: the Host's clock
/// when answering, against which members read the times in it (neither from a Host before them).
/// </summary>
public sealed record GroupStateResponse(
    string Status, long Version, string Name, IReadOnlyList<GroupMemberEntry> Members, GroupRoster? Roster = null, DateTimeOffset? Now = null);

public sealed record GroupMemberEntry(string DeviceId, string Name, bool IsHost, string? Address, int DiscoveryPort);

/// <summary>
/// The member list signed by the Host (docs/07-groups.md §4.1), also the body of <c>POST /api/v1/groups/{groupId}/roster</c>:
/// members pass it on, so a removal reaches those who cannot reach the Host. The higher <see cref="Version"/> wins.
/// <see cref="Removed"/>: who left or was removed in the last 31 days, and when, by the Host's clock.
/// </summary>
public sealed record GroupRoster(
    string GroupId,
    long Version,
    string Name,
    string HostId,
    byte[] HostKey,
    IReadOnlyList<GroupRosterMember> Members,
    IReadOnlyList<GroupRosterRemoved> Removed,
    byte[] Sig);

public sealed record GroupRosterMember(string DeviceId, string Name, bool IsHost);

public sealed record GroupRosterRemoved(string DeviceId, DateTimeOffset At);

public static class GroupStatus
{
    public const string Member = "member";
    public const string Pending = "pending";
    public const string Declined = "declined";
}

/// <summary>Body of <c>POST /api/v1/groups/invitations</c>: a Host invites the callee into one of its groups.</summary>
public sealed record GroupInvitationBody(string GroupId, string GroupName, string HostName);

/// <summary>
/// Body of <c>POST /api/v1/audio/listeners</c>: the caller wants to hear the callee's sound, sealed with
/// <paramref name="Key"/> (32 bytes, base64) and sent to <paramref name="Port"/> (UDP) at the caller's address.
/// </summary>
/// <param name="Password">The source's password, when it has one.</param>
public sealed record AudioListenBody(string SessionId, string Key, int Port, string? Password = null);

/// <summary>Answer to <c>POST /api/v1/audio/listeners</c>; <paramref name="Control"/>: the listener may press media keys here.</summary>
public sealed record AudioJoinResponse(bool Control);

/// <summary>Body of <c>POST /api/v1/audio/listeners/{sessionId}/keys</c>: a name from <see cref="Core.Platform.MediaKeyNames"/>.</summary>
public sealed record AudioKeyBody(string Key);

/// <summary>
/// Body of <c>POST /api/v1/chat/messages</c> (docs/15-chat.md §4): one message, its id made by the sender (32 hex digits,
/// time ordered). A <see cref="ChatKinds.Voice"/> message carries its Ogg Opus file in <paramref name="Audio"/>, how long
/// it plays and its waveform (base64 in JSON); kinds this version does not know are kept for a newer one.
/// </summary>
public sealed record ChatMessageBody(
    string Id, DateTimeOffset SentAt, string Kind, string? Text = null, int? DurationMs = null, byte[]? Wave = null, byte[]? Audio = null);

/// <summary>Body of <c>POST /api/v1/chat/receipts</c>: the caller has read our messages up to this id.</summary>
public sealed record ChatReceiptBody(string ReadUpTo);

/// <summary>Body of <c>POST /api/v1/chat/recalls</c>: the caller takes back these messages it sent ("Thu hồi").</summary>
/// <param name="Offers">Offers taken back from the conversation (docs/15-chat.md §6.3); a version without them ignores it.</param>
public sealed record ChatRecallBody(IReadOnlyList<string> Ids, IReadOnlyList<string>? Offers = null);

/// <summary>
/// Body of <c>POST /api/v1/chat/groups/{groupId}/messages</c> (docs/16-group-chat.md §4): a message of
/// <see cref="ChatMessageBody"/> to a whole group, signed by its author (the caller) with its device key
/// (<paramref name="AuthorKey"/>, a SubjectPublicKeyInfo whose hash is <paramref name="AuthorId"/>), so it stays the
/// author's whoever passes it on. <paramref name="AuthorName"/> is not signed: only shown while the member list has none.
/// </summary>
public sealed record GroupChatMessageBody(
    string Id, string AuthorId, string AuthorName, byte[] AuthorKey, DateTimeOffset SentAt, string Kind, byte[] Sig,
    string? Text = null, int? DurationMs = null, byte[]? Wave = null, byte[]? Audio = null);

/// <summary>Body of <c>POST /api/v1/chat/groups/{groupId}/recalls</c>: messages taken back, each with its signed recall.</summary>
/// <param name="Offers">The caller's offers to the group taken back from the conversation (docs/16-group-chat.md §6.3).</param>
public sealed record GroupChatRecallBody(IReadOnlyList<GroupChatRecall> Recalls, IReadOnlyList<string>? Offers = null);

public sealed record GroupChatRecall(string Id, byte[] Sig);

/// <summary>
/// Answer to <c>GET /api/v1/chat/groups/{groupId}/index?after=</c> (docs/16-group-chat.md §3.6): what the member holds
/// that was stored or taken back after <c>after</c>, oldest first. Ask again from <paramref name="Next"/>, at once while
/// <paramref name="More"/>.
/// </summary>
public sealed record GroupChatIndexResponse(IReadOnlyList<GroupChatIndexEntry> Entries, long Next, bool More);

public sealed record GroupChatIndexEntry(long Seq, string Id, string AuthorId, DateTimeOffset SentAt, bool Recalled);

/// <summary>Body of <c>POST /api/v1/chat/groups/{groupId}/fetch</c>: messages wanted from the index.</summary>
public sealed record GroupChatFetchBody(IReadOnlyList<string> Ids);

/// <summary>
/// Answer to <c>POST /api/v1/chat/groups/{groupId}/fetch</c>: the messages as their authors signed them, and those
/// taken back as their signed recalls. Ids not given back (too much at once, or gone) are asked for again.
/// </summary>
public sealed record GroupChatFetchResponse(IReadOnlyList<GroupChatMessageBody> Messages, IReadOnlyList<GroupChatRecalledEntry> Recalled);

/// <summary>A message its author took back: only who, when, and the author's signed recall.</summary>
public sealed record GroupChatRecalledEntry(string Id, string AuthorId, string AuthorName, byte[] AuthorKey, DateTimeOffset SentAt, byte[] Sig);

public static class ChatKinds
{
    public const string Text = "text";
    public const string Voice = "voice";

    /// <summary>Taken back by its sender: shown as "Tin nhắn đã được thu hồi" on both sides. Never sent as a message.</summary>
    public const string Recalled = "recalled";
}

/// <summary>Stable error codes in problem+json responses (docs/05-protocol.md §3).</summary>
public static class ApiErrorCodes
{
    public const string NotTrusted = "not_trusted";
    public const string RateLimited = "rate_limited";
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string NotRecipient = "not_recipient";

    /// <summary>
    /// The caller is not in that group, as far as this device's member list says. Unlike <see cref="NotTrusted"/>, which
    /// is also what a version without the route answers, it may be a list a few seconds behind: worth trying again.
    /// </summary>
    public const string NotMember = "not_member";
    public const string OfferClosed = "offer_closed";
    public const string SourceChanged = "source_changed";
    public const string Busy = "busy";
    public const string PasswordRequired = "password_required";
    public const string WrongPassword = "wrong_password";
    public const string Internal = "internal_error";
}
