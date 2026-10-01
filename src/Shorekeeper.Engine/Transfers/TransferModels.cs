using Shorekeeper.Core.Identity;

namespace Shorekeeper.Engine.Transfers;

// ───────────── Sending ─────────────

public enum OfferState
{
    Open,
    Withdrawn,
    Expired,

    /// <summary>Every recipient finished or declined.</summary>
    Done,
}

public enum RecipientState
{
    /// <summary>Not delivered yet (recipient offline); retried when it comes online.</summary>
    Pending,
    Delivered,
    Downloading,
    Completed,

    /// <summary>Took some of the files; may come back for the rest while the offer is open.</summary>
    Partial,
    Failed,
    Declined,
}

/// <param name="IsLocked">Another program has it open for writing; it can only be sent as a temporary copy.</param>
public sealed record PreparedFile(string RelativePath, string SourcePath, long Size, DateTimeOffset ModifiedAt, bool IsDirectory, bool IsLocked);

/// <param name="Unreadable">Paths skipped because they could not be read (no permission, gone).</param>
public sealed record PreparedOffer(IReadOnlyList<PreparedFile> Files, IReadOnlyList<string> Unreadable)
{
    public long TotalBytes => Files.Sum(f => f.Size);

    public IReadOnlyList<PreparedFile> LockedFiles => [.. Files.Where(f => f.IsLocked)];
}

public sealed record SentRecipient(DeviceId DeviceId, RecipientState State, long BytesServed);

/// <summary>What the "Đã gửi" page shows about one offer.</summary>
public sealed record SentOffer(
    string OfferId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? Note,
    long TotalBytes,
    IReadOnlyList<string> Paths,
    OfferState State,
    bool HasChangedFiles,
    IReadOnlyList<SentRecipient> Recipients);

// ───────────── Receiving ─────────────

public enum InboxState
{
    New,
    Downloading,
    Completed,

    /// <summary>The files the user picked arrived; the others can still be downloaded while the offer is open.</summary>
    Partial,

    /// <summary>Some files failed (or the whole download did); can be retried while the offer is open.</summary>
    Failed,
    Cancelled,
    Declined,
    Withdrawn,
    Expired,
}

public enum InboxFileState
{
    Available,
    Downloading,
    Completed,
    Failed,
}

public sealed record InboxFile(string FileId, string RelativePath, long Size, bool IsDirectory, InboxFileState State, string? FinalPath, string? Error);

/// <summary>What the "Hộp nhận" page shows about one offer.</summary>
/// <param name="ReconnectingUntil">Set while the connection dropped and the download is retrying.</param>
public sealed record ReceivedOffer(
    DeviceId SenderId,
    string OfferId,
    string? Note,
    long TotalBytes,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt,
    InboxState State,
    long BytesReceived,
    DateTimeOffset? ReconnectingUntil,
    string? Error,
    IReadOnlyList<InboxFile> Files);
