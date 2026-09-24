using System.Globalization;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Shorekeeper.Engine.Storage;

public sealed record Migration(int Version, string Name, string Sql);

/// <summary>
/// Applies embedded <c>Storage/Migrations/NNNN_name.sql</c> scripts in order, each in its own transaction.
/// Applied versions are recorded in <c>SchemaVersion</c>.
/// </summary>
public sealed class MigrationRunner
{
    private const string ResourcePrefix = "Migrations.";

    private readonly SqliteDatabase database;
    private readonly ILogger<MigrationRunner> logger;
    private readonly IReadOnlyList<Migration> migrations;

    public MigrationRunner(SqliteDatabase database, ILogger<MigrationRunner> logger)
        : this(database, logger, LoadEmbeddedMigrations())
    {
    }

    internal MigrationRunner(SqliteDatabase database, ILogger<MigrationRunner> logger, IReadOnlyList<Migration> migrations)
    {
        this.database = database;
        this.logger = logger;
        this.migrations = [.. migrations.OrderBy(m => m.Version)];
    }

    /// <returns>Number of migrations applied.</returns>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);

        // Persistent setting: WAL lets the UI read while the engine writes.
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;");
        await connection.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAt INTEGER NOT NULL);");

        var applied = (await connection.QueryAsync<int>("SELECT Version FROM SchemaVersion;")).ToHashSet();
        int count = 0;

        foreach (Migration migration in migrations.Where(m => !applied.Contains(m.Version)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(migration.Sql, transaction: transaction);
            await connection.ExecuteAsync(
                "INSERT INTO SchemaVersion (Version, Name, AppliedAt) VALUES (@Version, @Name, @AppliedAt);",
                new { migration.Version, migration.Name, AppliedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                transaction);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Applied database migration {Version} {Name}", migration.Version, migration.Name);
            count++;
        }

        return count;
    }

    internal static IReadOnlyList<Migration> LoadEmbeddedMigrations()
    {
        Assembly assembly = typeof(MigrationRunner).Assembly;
        var result = new List<Migration>();

        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fileName = resource[ResourcePrefix.Length..^".sql".Length];
            int separator = fileName.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(fileName[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out int version))
            {
                throw new InvalidOperationException($"Migration resource '{resource}' must be named NNNN_description.sql.");
            }

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            result.Add(new Migration(version, fileName[(separator + 1)..], reader.ReadToEnd()));
        }

        var duplicate = result.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate migration version {duplicate.Key}.");
        }

        return result;
    }
}
