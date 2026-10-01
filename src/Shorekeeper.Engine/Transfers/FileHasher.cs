using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shorekeeper.Engine.Storage;

namespace Shorekeeper.Engine.Transfers;

/// <summary>
/// SHA-256 of files we send, computed once in the background and cached by (path, size, modified time),
/// shared by every recipient (docs/06-file-transfer.md §6).
/// </summary>
public sealed class FileHasher(SqliteDatabase database, TimeProvider timeProvider, ILogger<FileHasher> logger)
{
    private readonly ConcurrentDictionary<string, Task<string>> running = new();

    /// <summary>
    /// Starts hashing if needed and waits up to <paramref name="wait"/> for it. Returns the hash when known,
    /// otherwise null (ask again later). Waiting avoids a poll round-trip for every small file.
    /// </summary>
    public async Task<string?> TryGetAsync(string path, long size, DateTimeOffset modifiedAt, TimeSpan wait, CancellationToken cancellationToken)
    {
        long modified = modifiedAt.ToUnixTimeMilliseconds();
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            var cached = await connection.QueryFirstOrDefaultAsync<(string Sha256, long LastUsedAt)?>(
                "SELECT Sha256, LastUsedAt FROM HashCache WHERE Path = @path AND Size = @size AND ModifiedAt = @modified;",
                new { path, size, modified });
            if (cached is { } hit)
            {
                // Reads stay reads: "last used" only needs day precision for the 30-day clean-up.
                long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                if (now - hit.LastUsedAt > TimeSpan.FromDays(1).TotalMilliseconds)
                {
                    await connection.ExecuteAsync(
                        "UPDATE HashCache SET LastUsedAt = @now WHERE Path = @path AND Size = @size AND ModifiedAt = @modified;",
                        new { path, size, modified, now });
                }

                return hit.Sha256;
            }
        }

        string key = $"{path}|{size}|{modified}";
        Task<string> task = running.GetOrAdd(key, _ => Task.Run(() => ComputeAsync(path, size, modified), CancellationToken.None));
        if (!task.IsCompleted && wait > TimeSpan.Zero)
        {
            await Task.WhenAny(task, Task.Delay(wait, timeProvider, cancellationToken));
        }

        if (!task.IsCompleted)
        {
            return null;
        }

        running.TryRemove(key, out _);
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        logger.LogWarning(task.Exception, "Hashing {Path} failed", path);
        return null;
    }

    /// <summary>Starts hashing in the background without waiting.</summary>
    public void Start(string path, long size, DateTimeOffset modifiedAt) =>
        _ = TryGetAsync(path, size, modifiedAt, TimeSpan.Zero, CancellationToken.None);

    private async Task<string> ComputeAsync(string path, long size, long modified)
    {
        string hash;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        }

        await using SqliteConnection connection = await database.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO HashCache (Path, Size, ModifiedAt, Sha256, LastUsedAt) VALUES (@path, @size, @modified, @hash, @now)
            ON CONFLICT (Path, Size, ModifiedAt) DO UPDATE SET Sha256 = excluded.Sha256, LastUsedAt = excluded.LastUsedAt;
            """,
            new { path, size, modified, hash, now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds() });
        return hash;
    }
}
