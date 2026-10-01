using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;
using Shorekeeper.Core.Transfers;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Transfers;

public sealed record OfferKey(DeviceId SenderId, string OfferId);

/// <summary>
/// Receiving side (docs/06-file-transfer.md): offers land in the inbox; the user decides whether to download.
/// Downloads resume within the reconnect window, are checked against the sender's SHA-256, never overwrite
/// an existing file and are marked as coming from another computer.
/// </summary>
public sealed class InboxService(
    SqliteDatabase database,
    PeerClient client,
    TrustStore trust,
    FileDownloader downloader,
    SettingsService settings,
    IFileTagger fileTagger,
    IPolicyProvider policy,
    TimeProvider timeProvider,
    ILogger<InboxService> logger)
{
    public const int ParallelFiles = 4;

    private const long FreeSpaceMargin = 1024L * 1024 * 1024;
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HashWait = TimeSpan.FromMinutes(10);

    private readonly Lock gate = new();
    private readonly Dictionary<OfferKey, Item> items = [];

    /// <summary>A new offer arrived (for the "file đến" popup).</summary>
    public event EventHandler<ReceivedOffer>? OfferArrived;

    /// <summary>Raised whenever an offer's state or progress changes.</summary>
    public event EventHandler<OfferKey>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        long since = (timeProvider.GetUtcNow() - HistoryRetention).ToUnixTimeMilliseconds();
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);

        // Downloads do not survive a restart (docs/06-file-transfer.md §5): whatever was running failed.
        await connection.ExecuteAsync("UPDATE InboxFiles SET State = 'failed', Error = 'App đã tắt khi đang tải.' WHERE State = 'downloading';");
        await connection.ExecuteAsync("UPDATE InboxOffers SET State = 'failed' WHERE State = 'downloading';");

        var offerRows = await connection.QueryAsync<InboxOfferRow>(
            "SELECT SenderId, OfferId, Note, TotalBytes, ReceivedAt, ExpiresAt, State FROM InboxOffers WHERE ReceivedAt >= @since;", new { since });
        var fileRows = (await connection.QueryAsync<InboxFileRow>(
            "SELECT SenderId, OfferId, FileId, RelativePath, Size, ModifiedAt, State, FinalPath, Error FROM InboxFiles;"))
            .ToLookup(f => (f.SenderId, f.OfferId));

        lock (gate)
        {
            foreach (InboxOfferRow row in offerRows)
            {
                var key = new OfferKey(DeviceId.Parse(row.SenderId), row.OfferId);
                var item = new Item(key, row.Note, DateTimeOffset.FromUnixTimeMilliseconds(row.ReceivedAt), DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresAt))
                {
                    State = Enum.Parse<InboxState>(row.State, true),
                };
                foreach (InboxFileRow file in fileRows[(row.SenderId, row.OfferId)])
                {
                    if (RelativePath.TrySanitize(file.RelativePath, out string[]? segments, out bool isDirectory))
                    {
                        item.Files.Add(new FileItem(file.FileId, file.RelativePath, segments, file.Size, isDirectory,
                            file.ModifiedAt is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null)
                        {
                            State = Enum.Parse<InboxFileState>(file.State, true),
                            FinalPath = file.FinalPath,
                            Error = file.Error,
                            BytesReceived = file.State == "completed" ? file.Size : 0,
                        });
                    }
                }

                items[key] = item;
            }
        }

        DeleteLeftoverPartialFiles();
    }

    public IReadOnlyList<ReceivedOffer> GetOffers()
    {
        lock (gate)
        {
            return [.. items.Values.OrderByDescending(i => i.ReceivedAt).Select(i => i.ToSnapshot())];
        }
    }

    public ReceivedOffer? Get(OfferKey key)
    {
        lock (gate)
        {
            return items.TryGetValue(key, out Item? item) ? item.ToSnapshot() : null;
        }
    }

    // ───────────── User actions ─────────────

    /// <summary>
    /// Downloads (or retries) the files not received yet; <paramref name="fileIds"/> limits it to the files the
    /// user picked ("Chọn file…"). Returns immediately.
    /// </summary>
    public void Download(OfferKey key, IReadOnlyCollection<string>? fileIds = null)
    {
        Item? item;
        lock (gate)
        {
            if (!items.TryGetValue(key, out item)
                || item.State is not (InboxState.New or InboxState.Partial or InboxState.Failed or InboxState.Cancelled)
                || item.ExpiresAt <= timeProvider.GetUtcNow())
            {
                return;
            }

            item.State = InboxState.Downloading;
            item.Error = null;
            item.Cancellation = new CancellationTokenSource();
        }

        HashSet<string>? selection = fileIds is null ? null : new HashSet<string>(fileIds, StringComparer.Ordinal);
        _ = Task.Run(() => DownloadOfferAsync(item, selection, item.Cancellation.Token));
    }

    public void Cancel(OfferKey key)
    {
        lock (gate)
        {
            if (items.TryGetValue(key, out Item? item))
            {
                item.Cancellation?.Cancel();
            }
        }
    }

    public async Task DeclineAsync(OfferKey key)
    {
        lock (gate)
        {
            if (!items.TryGetValue(key, out Item? item) || item.State != InboxState.New)
            {
                return;
            }

            item.State = InboxState.Declined;
        }

        await SaveOfferStateAsync(key, InboxState.Declined);
        await SendReceiptAsync(key, ReceiptStatus.Declined);
    }

    /// <summary>Marks offers past their expiry so the inbox stops offering to download them.</summary>
    public async Task ExpireAsync()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        OfferKey[] expired;
        lock (gate)
        {
            expired = [.. items.Values.Where(i => (i.State is InboxState.New or InboxState.Partial or InboxState.Failed or InboxState.Cancelled) && i.ExpiresAt <= now).Select(i => i.Key)];
            foreach (OfferKey key in expired)
            {
                items[key].State = InboxState.Expired;
            }
        }

        foreach (OfferKey key in expired)
        {
            await SaveOfferStateAsync(key, InboxState.Expired);
        }
    }

    // ───────────── Called by the API ─────────────

    internal async Task<bool> HandleOfferAsync(DeviceId sender, OfferManifest manifest)
    {
        if (!TryValidate(manifest, out List<FileItem>? files))
        {
            return false;
        }

        var key = new OfferKey(sender, manifest.OfferId);
        var item = new Item(key, manifest.Note, timeProvider.GetUtcNow(), manifest.ExpiresAt) { State = InboxState.New };
        item.Files.AddRange(files);
        lock (gate)
        {
            // Delivery is retried by the sender; the second copy is simply acknowledged.
            if (!items.TryAdd(key, item))
            {
                return true;
            }
        }

        await InsertAsync(item);
        logger.LogInformation("Offer {Offer} received from {Sender}: {Files} entries, {Bytes} bytes", key.OfferId, sender, files.Count, item.TotalBytes);

        // "Tự nhận file" for this contact (docs/06-file-transfer.md §10).
        if (trust.Get(sender) is { TrustLevel: TrustLevel.Trusted, AutoAcceptMaxBytes: { } limit } && item.TotalBytes <= limit)
        {
            Download(key);
        }

        OfferArrived?.Invoke(this, item.ToSnapshot());
        Changed?.Invoke(this, key);
        return true;
    }

    internal async Task HandleWithdrawnAsync(DeviceId sender, string offerId)
    {
        var key = new OfferKey(sender, offerId);
        lock (gate)
        {
            if (!items.TryGetValue(key, out Item? item) || item.State is InboxState.Completed or InboxState.Declined)
            {
                return;
            }

            item.State = InboxState.Withdrawn;
            item.Cancellation?.Cancel();
        }

        await SaveOfferStateAsync(key, InboxState.Withdrawn);
    }

    // ───────────── Downloading ─────────────

    private async Task DownloadOfferAsync(Item item, HashSet<string>? selection, CancellationToken cancellationToken)
    {
        Changed?.Invoke(this, item.Key);
        await SaveOfferStateAsync(item.Key, InboxState.Downloading);
        try
        {
            string root = DestinationRoot(item.Key.SenderId);
            Directory.CreateDirectory(root);
            List<FileItem> wanted = [.. item.Files.Where(f => f.State != InboxFileState.Completed && (selection is null || selection.Contains(f.FileId)))];
            EnsureFreeSpace(root, wanted.Sum(f => f.Size));
            await SendReceiptAsync(item.Key, ReceiptStatus.Downloading);

            // Folders of picked files are created on the way; empty folders only come with a full download.
            foreach (FileItem dir in wanted.Where(f => f.IsDirectory))
            {
                Directory.CreateDirectory(SafeCombine(root, dir.Segments));
                await SetFileResultAsync(item, dir, InboxFileState.Completed, null, null);
            }

            await Parallel.ForEachAsync(
                wanted.Where(f => !f.IsDirectory).ToList(),
                new ParallelOptions { MaxDegreeOfParallelism = ParallelFiles, CancellationToken = cancellationToken },
                (file, ct) => new ValueTask(DownloadFileAsync(item, root, file, ct)));
        }
        catch (DownloadFailedException ex)
        {
            item.Error = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.Error = $"Không tạo được thư mục nhận: {ex.Message}";
        }

        InboxState final;
        lock (gate)
        {
            int done = item.Files.Count(f => f.State == InboxFileState.Completed);
            bool pickedAllArrived = selection is not null
                && item.Files.Where(f => selection.Contains(f.FileId)).All(f => f.State == InboxFileState.Completed);
            final = item.State == InboxState.Withdrawn ? InboxState.Withdrawn
                : done == item.Files.Count ? InboxState.Completed
                : cancellationToken.IsCancellationRequested ? InboxState.Cancelled
                : pickedAllArrived ? InboxState.Partial
                : InboxState.Failed;
            if (final == InboxState.Failed && item.Error is null)
            {
                // One file: its own reason. Several: how many arrived, then the first reason.
                string? reason = item.Files.FirstOrDefault(f => f.Error is not null)?.Error;
                int files = item.Files.Count(f => !f.IsDirectory);
                int filesDone = item.Files.Count(f => !f.IsDirectory && f.State == InboxFileState.Completed);
                item.Error = files == 1 ? reason : $"{filesDone}/{files} file đã nhận. {reason}";
            }

            item.State = final;
            item.ReconnectingUntil = null;
            item.Cancellation?.Dispose();
            item.Cancellation = null;
        }

        await SaveOfferStateAsync(item.Key, final);
        await SendReceiptAsync(item.Key, final switch
        {
            InboxState.Completed => ReceiptStatus.Completed,
            InboxState.Partial => ReceiptStatus.Partial,
            _ => ReceiptStatus.Failed,
        });
        logger.LogInformation("Offer {Offer} from {Sender}: {State}", item.Key.OfferId, item.Key.SenderId, final);
    }

    private async Task DownloadFileAsync(Item item, string root, FileItem file, CancellationToken cancellationToken)
    {
        string target = SafeCombine(root, file.Segments);
        string folder = Path.GetDirectoryName(target)!;
        string temp = $"{target}.{item.Key.OfferId[..8]}.skpart";
        file.State = InboxFileState.Downloading;
        file.Error = null;
        file.BytesReceived = 0;
        Changed?.Invoke(this, item.Key);

        try
        {
            Directory.CreateDirectory(folder);
            var progress = new SyncProgress(p =>
            {
                file.BytesReceived = p.BytesReceived;
                item.ReconnectingUntil = p.ReconnectingUntil;
                ReportProgress(item);
            });
            string hash = await downloader.DownloadAsync(
                (offset, ifRange, ct) => RequestRangeAsync(item.Key, file.FileId, offset, ifRange, ct),
                temp, file.Size, settings.Current.ReconnectWindow, progress, cancellationToken);

            string expected = await WaitForHashAsync(item.Key, file.FileId, cancellationToken);
            if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new DownloadFailedException("Dữ liệu bị hỏng khi truyền (checksum không khớp).");
            }

            string final = UniquePath(target);
            File.Move(temp, final);
            if (file.ModifiedAt is { } modified)
            {
                File.SetLastWriteTimeUtc(final, modified.UtcDateTime);
            }

            if (policy.GetBoolean(PolicyNames.ApplyMarkOfTheWeb) != false)
            {
                fileTagger.MarkAsDownloaded(final);
            }

            await SetFileResultAsync(item, file, InboxFileState.Completed, final, null, hash);
        }
        catch (Exception ex) when (ex is DownloadFailedException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            await SetFileResultAsync(item, file, InboxFileState.Failed, null, ex is OperationCanceledException ? null : ex.Message);
            if (ex is OperationCanceledException)
            {
                throw;
            }
        }
    }

    private Task<HttpResponseMessage> RequestRangeAsync(OfferKey key, string fileId, long offset, EntityTagHeaderValue? ifRange, CancellationToken cancellationToken) =>
        client.SendAsync(
            key.SenderId,
            baseUri =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"offers/{key.OfferId}/files/{fileId}"));
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.IfRange = ifRange is null ? null : new RangeConditionHeaderValue(ifRange);
                }

                return request;
            },
            TimeSpan.FromSeconds(15),
            cancellationToken,
            HttpCompletionOption.ResponseHeadersRead);

    /// <summary>The sender hashes in the background; ask until it is ready.</summary>
    private async Task<string> WaitForHashAsync(OfferKey key, string fileId, CancellationToken cancellationToken)
    {
        DateTimeOffset giveUpAt = timeProvider.GetUtcNow() + HashWait;
        while (true)
        {
            try
            {
                using HttpResponseMessage response = await client.SendAsync(
                    key.SenderId, b => new HttpRequestMessage(HttpMethod.Get, new Uri(b, $"offers/{key.OfferId}/files/{fileId}/hash")),
                    TimeSpan.FromSeconds(15), cancellationToken);
                if (response.StatusCode != HttpStatusCode.Accepted)
                {
                    return (await PeerClient.ReadAsync<FileHashResponse>(response, cancellationToken)).Sha256;
                }
            }
            catch (PeerUnreachableException) when (timeProvider.GetUtcNow() < giveUpAt)
            {
            }
            catch (PeerApiException ex)
            {
                throw new DownloadFailedException(ex.Code == ApiErrorCodes.SourceChanged ? "File gốc đã bị thay đổi." : "Không lấy được checksum từ người gửi.", ex);
            }

            if (timeProvider.GetUtcNow() >= giveUpAt)
            {
                throw new DownloadFailedException("Không lấy được checksum từ người gửi.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
        }
    }

    private async Task SendReceiptAsync(OfferKey key, string status)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                key.SenderId,
                b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"offers/{key.OfferId}/receipts")) { Content = JsonContent.Create(new OfferReceipt(status)) },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
        }
        catch (PeerUnreachableException)
        {
            // Informational only.
        }
    }

    // ───────────── Helpers ─────────────

    private string DestinationRoot(DeviceId sender)
    {
        string root = settings.Current.DownloadDirectory;
        if (!settings.Current.CreateSenderSubfolders)
        {
            return root;
        }

        string name = trust.Get(sender)?.ShownName ?? sender.ShortForm;
        return RelativePath.TrySanitize(name.Replace('/', '-'), out string[]? segments, out _) ? Path.Combine(root, segments[0]) : root;
    }

    private static void EnsureFreeSpace(string root, long needed)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        if (drive.AvailableFreeSpace < needed + FreeSpaceMargin)
        {
            throw new DownloadFailedException($"Không đủ dung lượng trống trên ổ {drive.Name} (cần {needed / (1024 * 1024):N0} MB và chừa 1 GB).");
        }
    }

    /// <summary>Joins sanitized segments and double-checks the result stays inside the download folder.</summary>
    private static string SafeCombine(string root, string[] segments)
    {
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine([fullRoot, .. segments]));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DownloadFailedException("Đường dẫn file không hợp lệ.");
        }

        return path;
    }

    /// <summary>Never overwrite: "app.zip" becomes "app (1).zip" when taken.</summary>
    private static string UniquePath(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string candidate = path;
        for (int i = 1; File.Exists(candidate) || Directory.Exists(candidate); i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){extension}");
        }

        return candidate;
    }

    private static bool TryValidate(OfferManifest manifest, out List<FileItem> files)
    {
        files = [];
        if (manifest.OfferId is not { Length: > 0 and <= 64 }
            || manifest.Files is not { Count: > 0 and <= OfferService.MaxEntries }
            || manifest.Files.Count(f => !f.RelativePath.EndsWith('/')) > OfferService.MaxFiles
            || manifest.Note is { Length: > 500 })
        {
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (OfferFileEntry entry in manifest.Files)
        {
            if (entry.FileId is not { Length: > 0 and <= 32 }
                || !ids.Add(entry.FileId)
                || entry.Size < 0
                || !RelativePath.TrySanitize(entry.RelativePath, out string[]? segments, out bool isDirectory)
                || (isDirectory && entry.Size != 0))
            {
                return false;
            }

            files.Add(new FileItem(entry.FileId, entry.RelativePath, segments, entry.Size, isDirectory, entry.ModifiedAt));
        }

        return true;
    }

    private void DeleteLeftoverPartialFiles()
    {
        string root = settings.Current.DownloadDirectory;
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (string partial in Directory.EnumerateFiles(root, "*.skpart", SearchOption.AllDirectories))
            {
                TryDelete(partial);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not clean up partial downloads in {Root}", root);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private void ReportProgress(Item item)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (gate)
        {
            if (now - item.LastProgress < ProgressEvery)
            {
                return;
            }

            item.LastProgress = now;
        }

        Changed?.Invoke(this, item.Key);
    }

    private async Task SetFileResultAsync(Item item, FileItem file, InboxFileState state, string? finalPath, string? error, string? hash = null)
    {
        file.State = state;
        file.FinalPath = finalPath;
        file.Error = error;
        if (state == InboxFileState.Completed)
        {
            file.BytesReceived = file.Size;
        }

        await using (SqliteConnection connection = await database.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE InboxFiles SET State = @state, FinalPath = @finalPath, Error = @error, Sha256 = @hash WHERE SenderId = @sender AND OfferId = @offerId AND FileId = @fileId;",
                new { state = Name(state), finalPath, error, hash, sender = item.Key.SenderId.Value, offerId = item.Key.OfferId, fileId = file.FileId });
        }

        Changed?.Invoke(this, item.Key);
    }

    private async Task SaveOfferStateAsync(OfferKey key, InboxState state)
    {
        await using (SqliteConnection connection = await database.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE InboxOffers SET State = @state WHERE SenderId = @sender AND OfferId = @offerId;",
                new { state = Name(state), sender = key.SenderId.Value, offerId = key.OfferId });
        }

        Changed?.Invoke(this, key);
    }

    private async Task InsertAsync(Item item)
    {
        await using SqliteConnection connection = await database.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO InboxOffers (SenderId, OfferId, Note, TotalBytes, ReceivedAt, ExpiresAt, State)
            VALUES (@sender, @offerId, @note, @total, @receivedAt, @expiresAt, 'new');
            """,
            new
            {
                sender = item.Key.SenderId.Value, offerId = item.Key.OfferId, note = item.Note, total = item.TotalBytes,
                receivedAt = item.ReceivedAt.ToUnixTimeMilliseconds(), expiresAt = item.ExpiresAt.ToUnixTimeMilliseconds(),
            },
            transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO InboxFiles (SenderId, OfferId, FileId, RelativePath, Size, ModifiedAt, State)
            VALUES (@sender, @offerId, @FileId, @RelativePath, @Size, @modifiedAt, 'available');
            """,
            item.Files.Select(f => new
            {
                sender = item.Key.SenderId.Value, offerId = item.Key.OfferId, f.FileId, f.RelativePath, f.Size,
                modifiedAt = f.ModifiedAt?.ToUnixTimeMilliseconds(),
            }),
            transaction);
        await transaction.CommitAsync();
    }

    private static string Name<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    private sealed class Item(OfferKey key, string? note, DateTimeOffset receivedAt, DateTimeOffset expiresAt)
    {
        public OfferKey Key { get; } = key;

        public string? Note { get; } = note;

        public DateTimeOffset ReceivedAt { get; } = receivedAt;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public List<FileItem> Files { get; } = [];

        public InboxState State { get; set; }

        public string? Error { get; set; }

        public DateTimeOffset? ReconnectingUntil { get; set; }

        public DateTimeOffset LastProgress { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }

        public long TotalBytes => Files.Sum(f => f.Size);

        public ReceivedOffer ToSnapshot() => new(
            Key.SenderId, Key.OfferId, Note, TotalBytes, ReceivedAt, ExpiresAt, State,
            Files.Sum(f => f.BytesReceived), ReconnectingUntil, Error,
            [.. Files.Select(f => new InboxFile(f.FileId, f.RelativePath, f.Size, f.IsDirectory, f.State, f.FinalPath, f.Error))]);
    }

    private sealed class FileItem(string fileId, string relativePath, string[] segments, long size, bool isDirectory, DateTimeOffset? modifiedAt)
    {
        public string FileId { get; } = fileId;

        public string RelativePath { get; } = relativePath;

        public string[] Segments { get; } = segments;

        public long Size { get; } = size;

        public bool IsDirectory { get; } = isDirectory;

        public DateTimeOffset? ModifiedAt { get; } = modifiedAt;

        public InboxFileState State { get; set; } = InboxFileState.Available;

        public string? FinalPath { get; set; }

        public string? Error { get; set; }

        public long BytesReceived { get; set; }
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed class InboxOfferRow
    {
        public string SenderId { get; init; } = "";

        public string OfferId { get; init; } = "";

        public string? Note { get; init; }

        public long TotalBytes { get; init; }

        public long ReceivedAt { get; init; }

        public long ExpiresAt { get; init; }

        public string State { get; init; } = "";
    }

    private sealed class InboxFileRow
    {
        public string SenderId { get; init; } = "";

        public string OfferId { get; init; } = "";

        public string FileId { get; init; } = "";

        public string RelativePath { get; init; } = "";

        public long Size { get; init; }

        public long? ModifiedAt { get; init; }

        public string State { get; init; } = "";

        public string? FinalPath { get; init; }

        public string? Error { get; init; }
    }
}
