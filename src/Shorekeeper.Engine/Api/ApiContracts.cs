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
public sealed record OfferManifest(
    string OfferId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? Note, long TotalSize, IReadOnlyList<OfferFileEntry> Files);

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
