using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Engine.Storage;

namespace Shorekeeper.Engine.Tests;

public sealed class MigrationRunnerTests : IDisposable
{
    private readonly TempAppFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void Embedded_migrations_are_found_in_order()
    {
        IReadOnlyList<Migration> migrations = MigrationRunner.LoadEmbeddedMigrations();

        Assert.NotEmpty(migrations);
        Assert.Contains(migrations, m => m.Version == 1 && m.Name == "initial");
    }

    [Fact]
    public async Task Fresh_database_gets_the_full_schema()
    {
        var database = new SqliteDatabase(folder.Paths);
        int applied = await new MigrationRunner(database, NullLogger<MigrationRunner>.Instance).MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MigrationRunner.LoadEmbeddedMigrations().Count, applied);

        await using SqliteConnection connection = await database.OpenAsync(TestContext.Current.CancellationToken);
        var tables = (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet();
        foreach (string table in new[] { "Peers", "PeerEndpoints", "Offers", "OfferRecipients", "InboxOffers", "InboxFiles", "Groups", "GroupMembers", "ActivityLog" })
        {
            Assert.Contains(table, tables);
        }

        string journalMode = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;") ?? "";
        Assert.Equal("wal", journalMode, ignoreCase: true);
    }

    [Fact]
    public async Task Running_twice_applies_nothing_the_second_time()
    {
        var database = new SqliteDatabase(folder.Paths);
        var runner = new MigrationRunner(database, NullLogger<MigrationRunner>.Instance);

        await runner.MigrateAsync(TestContext.Current.CancellationToken);
        int secondRun = await runner.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, secondRun);
    }

    [Fact]
    public async Task New_migrations_are_applied_on_top_of_existing_ones()
    {
        var database = new SqliteDatabase(folder.Paths);
        var v1 = new Migration(1, "first", "CREATE TABLE A (Id INTEGER PRIMARY KEY);");
        var v2 = new Migration(2, "second", "ALTER TABLE A ADD COLUMN Name TEXT;");

        await new MigrationRunner(database, NullLogger<MigrationRunner>.Instance, [v1]).MigrateAsync(TestContext.Current.CancellationToken);
        int applied = await new MigrationRunner(database, NullLogger<MigrationRunner>.Instance, [v2, v1]).MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, applied);
        await using SqliteConnection connection = await database.OpenAsync(TestContext.Current.CancellationToken);
        var versions = await connection.QueryAsync<int>("SELECT Version FROM SchemaVersion ORDER BY Version;");
        Assert.Equal([1, 2], versions);
    }

    [Fact]
    public async Task Failed_migration_is_rolled_back()
    {
        var database = new SqliteDatabase(folder.Paths);
        var broken = new Migration(1, "broken", "CREATE TABLE A (Id INTEGER); THIS IS NOT SQL;");

        await Assert.ThrowsAsync<SqliteException>(() =>
            new MigrationRunner(database, NullLogger<MigrationRunner>.Instance, [broken]).MigrateAsync(TestContext.Current.CancellationToken));

        await using SqliteConnection connection = await database.OpenAsync(TestContext.Current.CancellationToken);
        long tableCount = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'A';");
        long versionCount = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SchemaVersion;");
        Assert.Equal(0, tableCount);
        Assert.Equal(0, versionCount);
    }
}
