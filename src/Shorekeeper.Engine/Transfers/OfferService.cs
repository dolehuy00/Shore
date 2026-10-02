using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Storage;

namespace Shorekeeper.Engine.Transfers;

/// <summary>
/// Sending side (docs/06-file-transfer.md): the sender owns the offer and picks the recipients;
/// recipients pull the files over HTTPS with Range requests. Offers stay open until withdrawn,
/// expired or finished by everyone, and survive app restarts.
/// </summary>
public sealed class OfferService(
    SqliteDatabase database,
    PeerClient client,
    PeerDirectory directory,
    GroupStore groups,
    FileHasher hasher,
    SettingsService settings,
    AppPaths paths,
    TimeProvider timeProvider,
    ILogger<OfferService> logger)
{
    /// <summary>Files per offer (docs/05-protocol.md §5).</summary>
    public const int MaxFiles = 10_000;

    /// <summary>Files plus folders, which bounds the manifest size.</summary>
    public const int MaxEntries = 20_000;
    public const int MaxConcurrentStreams = 16;

    /// <summary>How long a hash request waits for the background hash before answering 202 (must stay below the client timeout).</summary>
    private static readonly TimeSpan HashWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "Thumbs.db" };

    private readonly Lock gate = new();
    private readonly Dictionary<string, Offer> offers = [];
    private readonly HashSet<(string, DeviceId)> delivering = [];
    private readonly Dictionary<string, DateTimeOffset> lastProgress = [];
    private int activeStreams;

    /// <summary>Raised with the offer id whenever it changes (state, recipients, progress).</summary>
    public event EventHandler<string>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        long since = (timeProvider.GetUtcNow() - HistoryRetention).ToUnixTimeMilliseconds();
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var offerRows = await connection.QueryAsync<OfferRow>(
            "SELECT OfferId, GroupId, Note, TotalBytes, State, CreatedAt, ExpiresAt FROM Offers WHERE CreatedAt >= @since;", new { since });
        var fileRows = (await connection.QueryAsync<OfferFileRow>(
            "SELECT OfferId, FileId, RelativePath, Size, ModifiedAt, SourcePath, SnapshotPath, Changed FROM OfferFiles;")).ToLookup(f => f.OfferId);
        var recipientRows = (await connection.QueryAsync<RecipientRow>(
            "SELECT OfferId, DeviceId, State FROM OfferRecipients;")).ToLookup(r => r.OfferId);

        lock (gate)
        {
            foreach (OfferRow row in offerRows)
            {
                var offer = new Offer(row.OfferId, FromMs(row.CreatedAt), FromMs(row.ExpiresAt), row.Note, Enum.Parse<OfferState>(row.State, true))
                {
                    GroupId = row.GroupId,
                    Files = [.. fileRows[row.OfferId].Select(f => f.ToFile())],
                };
                foreach (RecipientRow r in recipientRows[row.OfferId])
                {
                    offer.Recipients[DeviceId.Parse(r.DeviceId)] = new Recipient { State = Enum.Parse<RecipientState>(r.State, true) };
                }

                offers[offer.Id] = offer;
            }
        }

        // Snapshots of offers that are no longer open are just leftovers.
        foreach (string dir in Directory.Exists(paths.SnapshotsDirectory) ? Directory.GetDirectories(paths.SnapshotsDirectory) : [])
        {
            bool open;
            lock (gate)
            {
                open = offers.TryGetValue(Path.GetFileName(dir), out Offer? o) && o.State == OfferState.Open;
            }

            if (!open)
            {
                TryDeleteDirectory(dir);
            }
        }

        directory.Changed += (_, change) =>
        {
            if (change.Kind != PeerChangeKind.Removed && change.Peer.State == PeerState.Online)
            {
                _ = DeliverPendingAsync(change.Peer.DeviceId, CancellationToken.None);
            }
        };
    }

    public IReadOnlyList<SentOffer> GetOffers()
    {
        lock (gate)
        {
            return [.. offers.Values.OrderByDescending(o => o.CreatedAt).Select(o => o.ToSnapshot())];
        }
    }

    // ───────────── Creating ─────────────

    /// <summary>Lists what would be sent: folders are expanded, links and system clutter skipped, locked files flagged.</summary>
    /// <exception cref="InvalidOperationException">More than <see cref="MaxFiles"/> files or <see cref="MaxEntries"/> entries.</exception>
    public static PreparedOffer Prepare(IEnumerable<string> selectedPaths)
    {
        var files = new List<PreparedFile>();
        var unreadable = new List<string>();
        int fileCount = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string selected in selectedPaths)
        {
            string full = Path.GetFullPath(selected);
            if (Directory.Exists(full))
            {
                var root = new DirectoryInfo(full);
                AddDirectory(root, Unique(root.Name + "/"));
            }
            else if (File.Exists(full))
            {
                var file = new FileInfo(full);
                AddFile(file, Unique(file.Name));
            }
            else
            {
                unreadable.Add(selected);
            }

            CheckLimits();
        }

        return new PreparedOffer(files, unreadable);

        void CheckLimits()
        {
            if (files.Count > MaxEntries || fileCount > MaxFiles)
            {
                throw new InvalidOperationException($"Quá nhiều file: tối đa {MaxFiles:N0} file mỗi lần gửi.");
            }
        }

        void AddDirectory(DirectoryInfo dir, string relative)
        {
            files.Add(new PreparedFile(relative, dir.FullName, 0, ToMsPrecision(dir.LastWriteTimeUtc), IsDirectory: true, IsLocked: false));
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = [.. dir.EnumerateFileSystemInfos()];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                unreadable.Add(dir.FullName);
                return;
            }

            foreach (FileSystemInfo entry in entries)
            {
                // Junctions and symlinks could loop or point outside what the user chose.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || SkippedNames.Contains(entry.Name))
                {
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    AddDirectory(sub, relative + sub.Name + "/");
                }
                else if (entry is FileInfo file)
                {
                    AddFile(file, relative + file.Name);
                }

                CheckLimits();
            }
        }

        void AddFile(FileInfo file, string relative)
        {
            try
            {
                files.Add(new PreparedFile(relative, file.FullName, file.Length, ToMsPrecision(file.LastWriteTimeUtc), IsDirectory: false, IsLocked(file.FullName)));
                fileCount++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                unreadable.Add(file.FullName);
            }
        }

        // Two selected items with the same name ("a.txt" from two folders) must not collide at the recipient.
        string Unique(string name)
        {
            bool directory = name.EndsWith('/');
            string bare = directory ? name[..^1] : name;
            string candidate = bare;
            for (int i = 1; !usedNames.Add(candidate); i++)
            {
                candidate = directory ? $"{bare} ({i})" : $"{Path.GetFileNameWithoutExtension(bare)} ({i}){Path.GetExtension(bare)}";
            }

            return directory ? candidate + "/" : candidate;
        }
    }

    /// <summary>Creates the offer and starts delivering it.</summary>
    /// <param name="snapshotLocked">Send locked files as a temporary copy; otherwise they are left out.</param>
    /// <param name="groupId">Sent to members of this group, who need not be contacts (docs/07-groups.md §1).</param>
    public async Task<string> CreateAsync(
        PreparedOffer prepared, IReadOnlyList<DeviceId> recipients, string? note, bool snapshotLocked, string? groupId, CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        var offer = new Offer(Guid.NewGuid().ToString("N"), now, now + settings.Current.OfferLifetime, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), OfferState.Open)
        {
            GroupId = groupId,
        };
        bool snapshotAll = settings.Current.AlwaysSnapshotBeforeSend;

        foreach (PreparedFile file in prepared.Files)
        {
            if (file.IsLocked && !snapshotLocked)
            {
                continue;
            }

            var entry = new OfferFile($"f{offer.Files.Count}", file.RelativePath, file.Size, file.ModifiedAt, file.SourcePath, null, file.IsDirectory, false);
            if (!file.IsDirectory && (snapshotAll || file.IsLocked))
            {
                entry = await SnapshotAsync(offer.Id, entry, cancellationToken);
            }

            offer.Files.Add(entry);
        }

        foreach (DeviceId recipient in recipients.Distinct())
        {
            offer.Recipients[recipient] = new Recipient { State = RecipientState.Pending };
        }

        await InsertAsync(offer, cancellationToken);
        lock (gate)
        {
            offers[offer.Id] = offer;
        }

        logger.LogInformation("Offer {Offer} created: {Files} entries, {Bytes} bytes, {Recipients} recipients",
            offer.Id, offer.Files.Count, offer.TotalBytes, offer.Recipients.Count);
        Changed?.Invoke(this, offer.Id);
        foreach (DeviceId recipient in offer.Recipients.Keys)
        {
            _ = DeliverAsync(offer, recipient, CancellationToken.None);
        }

        return offer.Id;
    }

    public async Task WithdrawAsync(string offerId)
    {
        DeviceId[] notify;
        lock (gate)
        {
            if (!offers.TryGetValue(offerId, out Offer? offer) || offer.State != OfferState.Open)
            {
                return;
            }

            offer.State = OfferState.Withdrawn;
            // Pending included: the offer may have just arrived while its delivery response is still on the way.
            notify = [.. offer.Recipients.Where(r => r.Value.State != RecipientState.Declined).Select(r => r.Key)];
        }

        await SetOfferStateAsync(offerId, OfferState.Withdrawn);
        foreach (DeviceId recipient in notify)
        {
            try
            {
                using HttpResponseMessage response = await client.SendAsync(
                    recipient, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"inbox/offers/{offerId}/withdrawn")), TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (PeerUnreachableException)
            {
                // It learns when it next tries to download (410).
            }
        }
    }

    // ───────────── Background work ─────────────

    /// <summary>Delivers offers to recipients that were offline. <paramref name="onlyTo"/> limits it to one peer.</summary>
    public async Task DeliverPendingAsync(DeviceId? onlyTo, CancellationToken cancellationToken)
    {
        List<(Offer Offer, DeviceId Recipient)> pending;
        lock (gate)
        {
            pending = [.. offers.Values
                .Where(o => o.State == OfferState.Open)
                .SelectMany(o => o.Recipients.Where(r => r.Value.State == RecipientState.Pending && (onlyTo is null || r.Key == onlyTo)).Select(r => (o, r.Key)))];
        }

        foreach ((Offer offer, DeviceId recipient) in pending)
        {
            await DeliverAsync(offer, recipient, cancellationToken);
        }
    }

    public async Task ExpireAsync()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string[] expired;
        lock (gate)
        {
            expired = [.. offers.Values.Where(o => o.State == OfferState.Open && o.ExpiresAt <= now).Select(o => o.Id)];
            foreach (string id in expired)
            {
                offers[id].State = OfferState.Expired;
            }
        }

        foreach (string id in expired)
        {
            await SetOfferStateAsync(id, OfferState.Expired);
        }
    }

    private async Task DeliverAsync(Offer offer, DeviceId recipient, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!delivering.Add((offer.Id, recipient)))
            {
                return;
            }
        }

        try
        {
            var manifest = new OfferManifest(
                offer.Id, offer.CreatedAt, offer.ExpiresAt, offer.Note, offer.TotalBytes,
                [.. offer.Files.Select(f => new OfferFileEntry(f.FileId, f.RelativePath, f.Size, f.IsDirectory ? null : f.ModifiedAt))],
                offer.GroupId);
            using HttpResponseMessage response = await client.SendAsync(
                recipient,
                b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, "inbox/offers")) { Content = JsonContent.Create(manifest) },
                TimeSpan.FromSeconds(15),
                cancellationToken);
            await PeerClient.EnsureSuccessAsync(response, cancellationToken);
            await SetRecipientStateAsync(offer.Id, recipient, RecipientState.Delivered, onlyIf: RecipientState.Pending);
        }
        catch (PeerUnreachableException)
        {
            logger.LogDebug("Offer {Offer} not delivered yet to {Peer} (offline)", offer.Id, recipient);
        }
        catch (PeerApiException ex)
        {
            logger.LogWarning("Offer {Offer} refused by {Peer}: {Code}", offer.Id, recipient, ex.Code);
            await SetRecipientStateAsync(offer.Id, recipient, RecipientState.Failed);
        }
        finally
        {
            lock (gate)
            {
                delivering.Remove((offer.Id, recipient));
            }
        }
    }

    // ───────────── Serving (called by the API) ─────────────

    internal IResult ServeFile(DeviceId caller, string offerId, string fileId)
    {
        (IResult? error, (Offer, OfferFile, Recipient)? found) = Find(caller, offerId, fileId);
        if (error is not null)
        {
            return error;
        }

        (Offer offer, OfferFile file, Recipient recipient) = found!.Value;
        if (!IsUnchanged(file))
        {
            MarkChanged(offer, file);
            return ApiResults.Problem(StatusCodes.Status412PreconditionFailed, ApiErrorCodes.SourceChanged);
        }

        if (Interlocked.Increment(ref activeStreams) > MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref activeStreams);
            return ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.Busy);
        }

        FileStream stream;
        try
        {
            // FileShare.Read: other programs can read but not modify the file while it is being sent.
            stream = new FileStream(file.ServePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        }
        catch (IOException)
        {
            Interlocked.Decrement(ref activeStreams);
            return ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.Busy);
        }

        hasher.Start(file.ServePath, file.Size, file.ModifiedAt);
        if (recipient.State is RecipientState.Pending or RecipientState.Delivered)
        {
            _ = SetRecipientStateAsync(offerId, caller, RecipientState.Downloading);
        }

        var counting = new CountingStream(stream, read =>
        {
            Interlocked.Add(ref recipient.BytesServed, read);
            ReportProgress(offerId);
        }, () => Interlocked.Decrement(ref activeStreams));
        return Results.File(counting, "application/octet-stream", lastModified: file.ModifiedAt, entityTag: ETagFor(file), enableRangeProcessing: true);
    }

    internal async Task<IResult> GetHashAsync(DeviceId caller, string offerId, string fileId, CancellationToken cancellationToken)
    {
        (IResult? error, (Offer, OfferFile, Recipient)? found) = Find(caller, offerId, fileId);
        if (error is not null)
        {
            return error;
        }

        (Offer offer, OfferFile file, _) = found!.Value;
        if (!IsUnchanged(file))
        {
            MarkChanged(offer, file);
            return ApiResults.Problem(StatusCodes.Status412PreconditionFailed, ApiErrorCodes.SourceChanged);
        }

        string? hash = await hasher.TryGetAsync(file.ServePath, file.Size, file.ModifiedAt, HashWait, cancellationToken);
        return hash is null ? Results.Accepted() : Results.Ok(new FileHashResponse(hash));
    }

    internal async Task<IResult> HandleReceiptAsync(DeviceId caller, string offerId, OfferReceipt receipt)
    {
        RecipientState? state = receipt.Status switch
        {
            ReceiptStatus.Downloading => RecipientState.Downloading,
            ReceiptStatus.Completed => RecipientState.Completed,
            ReceiptStatus.Partial => RecipientState.Partial,
            ReceiptStatus.Failed => RecipientState.Failed,
            ReceiptStatus.Declined => RecipientState.Declined,
            _ => null,
        };

        lock (gate)
        {
            if (state is null || !offers.TryGetValue(offerId, out Offer? offer) || !offer.Recipients.ContainsKey(caller))
            {
                return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound);
            }
        }

        await SetRecipientStateAsync(offerId, caller, state.Value);
        return Results.NoContent();
    }

    private (IResult? Error, (Offer, OfferFile, Recipient)? Found) Find(DeviceId caller, string offerId, string fileId)
    {
        lock (gate)
        {
            if (!offers.TryGetValue(offerId, out Offer? offer))
            {
                return (ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound), null);
            }

            if (!offer.Recipients.TryGetValue(caller, out Recipient? recipient))
            {
                return (ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.NotRecipient), null);
            }

            // Still a contact, or still in the group the offer was sent to (removed members lose access).
            if (!groups.CanExchangeFiles(caller, offer.GroupId))
            {
                return (ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.NotTrusted), null);
            }

            if (offer.State != OfferState.Open || offer.ExpiresAt <= timeProvider.GetUtcNow())
            {
                return (ApiResults.Problem(StatusCodes.Status410Gone, ApiErrorCodes.OfferClosed), null);
            }

            OfferFile? file = offer.Files.FirstOrDefault(f => f.FileId == fileId && !f.IsDirectory);
            return file is null
                ? (ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound), null)
                : (null, (offer, file, recipient));
        }
    }

    // ───────────── Helpers ─────────────

    private static bool IsLocked(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020))
        {
            // ERROR_SHARING_VIOLATION: someone has it open for writing.
            return true;
        }
    }

    private static bool IsUnchanged(OfferFile file)
    {
        var info = new FileInfo(file.ServePath);
        return info.Exists && info.Length == file.Size && ToMsPrecision(info.LastWriteTimeUtc) == file.ModifiedAt;
    }

    private static EntityTagHeaderValue ETagFor(OfferFile file) =>
        new($"\"{file.Size:x}-{file.ModifiedAt.ToUnixTimeMilliseconds():x}\"");

    private async Task<OfferFile> SnapshotAsync(string offerId, OfferFile file, CancellationToken cancellationToken)
    {
        string dir = Path.Combine(paths.SnapshotsDirectory, offerId);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, file.FileId);

        // ReadWrite sharing: the point is to copy a file another program is still writing.
        await using (var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous))
        await using (var copy = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
        {
            await source.CopyToAsync(copy, cancellationToken);
        }

        File.SetLastWriteTimeUtc(target, file.ModifiedAt.UtcDateTime);
        var info = new FileInfo(target);
        return file with { SnapshotPath = target, Size = info.Length, ModifiedAt = ToMsPrecision(info.LastWriteTimeUtc) };
    }

    private void MarkChanged(Offer offer, OfferFile file)
    {
        lock (gate)
        {
            if (file.Changed)
            {
                return;
            }

            int index = offer.Files.IndexOf(file);
            offer.Files[index] = file with { Changed = true };
        }

        _ = ExecuteAsync("UPDATE OfferFiles SET Changed = 1 WHERE OfferId = @offerId AND FileId = @fileId;", new { offerId = offer.Id, fileId = file.FileId });
        Changed?.Invoke(this, offer.Id);
    }

    private async Task SetRecipientStateAsync(string offerId, DeviceId recipient, RecipientState state, RecipientState? onlyIf = null)
    {
        bool done;
        lock (gate)
        {
            if (!offers.TryGetValue(offerId, out Offer? offer) || !offer.Recipients.TryGetValue(recipient, out Recipient? r) || (onlyIf is not null && r.State != onlyIf))
            {
                return;
            }

            r.State = state;
            done = offer.State == OfferState.Open && offer.Recipients.Values.All(x => x.State is RecipientState.Completed or RecipientState.Declined);
            if (done)
            {
                offer.State = OfferState.Done;
            }
        }

        await ExecuteAsync(
            "UPDATE OfferRecipients SET State = @state, UpdatedAt = @now, DeliveredAt = COALESCE(DeliveredAt, CASE WHEN @state <> 'pending' THEN @now END) WHERE OfferId = @offerId AND DeviceId = @deviceId;",
            new { state = Name(state), now = NowMs(), offerId, deviceId = recipient.Value });
        if (done)
        {
            await SetOfferStateAsync(offerId, OfferState.Done);
        }
        else
        {
            Changed?.Invoke(this, offerId);
        }
    }

    private async Task SetOfferStateAsync(string offerId, OfferState state)
    {
        await ExecuteAsync("UPDATE Offers SET State = @state, ClosedAt = @now WHERE OfferId = @offerId;", new { state = Name(state), now = NowMs(), offerId });
        if (state != OfferState.Open)
        {
            TryDeleteDirectory(Path.Combine(paths.SnapshotsDirectory, offerId));
            logger.LogInformation("Offer {Offer} closed: {State}", offerId, state);
        }

        Changed?.Invoke(this, offerId);
    }

    private async Task InsertAsync(Offer offer, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO Offers (OfferId, GroupId, Note, TotalBytes, State, CreatedAt, ExpiresAt) VALUES (@Id, @GroupId, @Note, @TotalBytes, 'open', @createdAt, @expiresAt);",
            new { offer.Id, offer.GroupId, offer.Note, offer.TotalBytes, createdAt = offer.CreatedAt.ToUnixTimeMilliseconds(), expiresAt = offer.ExpiresAt.ToUnixTimeMilliseconds() },
            transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO OfferFiles (OfferId, FileId, RelativePath, Size, ModifiedAt, SourcePath, SnapshotPath)
            VALUES (@OfferId, @FileId, @RelativePath, @Size, @ModifiedAt, @SourcePath, @SnapshotPath);
            """,
            offer.Files.Select(f => new { OfferId = offer.Id, f.FileId, f.RelativePath, f.Size, ModifiedAt = f.ModifiedAt.ToUnixTimeMilliseconds(), f.SourcePath, f.SnapshotPath }),
            transaction);
        await connection.ExecuteAsync(
            "INSERT INTO OfferRecipients (OfferId, DeviceId, State) VALUES (@OfferId, @DeviceId, 'pending');",
            offer.Recipients.Keys.Select(r => new { OfferId = offer.Id, DeviceId = r.Value }),
            transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using SqliteConnection connection = await database.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private void ReportProgress(string offerId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (gate)
        {
            if (lastProgress.TryGetValue(offerId, out DateTimeOffset last) && now - last < ProgressEvery)
            {
                return;
            }

            lastProgress[offerId] = now;
        }

        Changed?.Invoke(this, offerId);
    }

    private long NowMs() => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static string Name<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    /// <summary>The database keeps milliseconds; compare file times at that precision.</summary>
    private static DateTimeOffset ToMsPrecision(DateTime utc) => FromMs(new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds());

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete {Directory}", dir);
        }
    }

    private sealed class Offer(string id, DateTimeOffset createdAt, DateTimeOffset expiresAt, string? note, OfferState state)
    {
        public string Id { get; } = id;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public string? Note { get; } = note;

        public string? GroupId { get; init; }

        public OfferState State { get; set; } = state;

        public List<OfferFile> Files { get; init; } = [];

        public Dictionary<DeviceId, Recipient> Recipients { get; } = [];

        public long TotalBytes => Files.Sum(f => f.Size);

        public SentOffer ToSnapshot() => new(
            Id, CreatedAt, ExpiresAt, Note, TotalBytes,
            [.. Files.Where(f => !f.RelativePath.Contains('/') || f.RelativePath.IndexOf('/') == f.RelativePath.Length - 1).Select(f => f.RelativePath)],
            State,
            Files.Any(f => f.Changed),
            [.. Recipients.Select(r => new SentRecipient(r.Key, r.Value.State, Interlocked.Read(ref r.Value.BytesServed)))]);
    }

    /// <param name="SnapshotPath">Temporary copy served instead of the original, if any.</param>
    private sealed record OfferFile(
        string FileId, string RelativePath, long Size, DateTimeOffset ModifiedAt, string SourcePath, string? SnapshotPath, bool IsDirectory, bool Changed)
    {
        public string ServePath => SnapshotPath ?? SourcePath;
    }

    private sealed class Recipient
    {
        public long BytesServed;

        public RecipientState State { get; set; }
    }

    private sealed class OfferRow
    {
        public string OfferId { get; init; } = "";

        public string? GroupId { get; init; }

        public string? Note { get; init; }

        public long TotalBytes { get; init; }

        public string State { get; init; } = "";

        public long CreatedAt { get; init; }

        public long ExpiresAt { get; init; }
    }

    private sealed class OfferFileRow
    {
        public string OfferId { get; init; } = "";

        public string FileId { get; init; } = "";

        public string RelativePath { get; init; } = "";

        public long Size { get; init; }

        public long ModifiedAt { get; init; }

        public string SourcePath { get; init; } = "";

        public string? SnapshotPath { get; init; }

        public long Changed { get; init; }

        public OfferFile ToFile() =>
            new(FileId, RelativePath, Size, FromMs(ModifiedAt), SourcePath, SnapshotPath, RelativePath.EndsWith('/'), Changed != 0);
    }

    private sealed class RecipientRow
    {
        public string OfferId { get; init; } = "";

        public string DeviceId { get; init; } = "";

        public string State { get; init; } = "";
    }
}

/// <summary>Counts bytes read (sender progress) and runs a callback when the response is done with it.</summary>
internal sealed class CountingStream(Stream inner, Action<int> onRead, Action onDispose) : Stream
{
    private int disposed;

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            inner.Dispose();
            onDispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await inner.DisposeAsync();
            onDispose();
        }

        await base.DisposeAsync();
    }

    private int Counted(int read)
    {
        if (read > 0)
        {
            onRead(read);
        }

        return read;
    }
}
