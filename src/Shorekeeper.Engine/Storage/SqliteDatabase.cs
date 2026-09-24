using Microsoft.Data.Sqlite;

namespace Shorekeeper.Engine.Storage;

/// <summary>Opens connections to the app's SQLite database with the pragmas we rely on.</summary>
public sealed class SqliteDatabase
{
    private readonly string connectionString;

    public SqliteDatabase(AppPaths paths)
        : this(paths.DatabaseFile)
    {
    }

    public SqliteDatabase(string databaseFile)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
