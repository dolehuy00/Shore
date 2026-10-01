using System.Net;
using Dapper;
using Microsoft.Data.Sqlite;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Storage;

namespace Shorekeeper.Engine.Trust;

/// <param name="Alias">Name the user chose for this peer; shown instead of <paramref name="DisplayName"/> when set.</param>
public sealed record PeerRecord(DeviceId DeviceId, string DisplayName, string HostName, string? Alias, TrustLevel TrustLevel)
{
    public string ShownName => string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias;
}

/// <summary>
/// Contacts, blocked peers, known addresses and manually added machines (docs/09-data-storage.md).
/// Contacts and blocked peers are cached in memory because every API request checks them.
/// </summary>
public sealed class TrustStore(SqliteDatabase database, TimeProvider timeProvider)
{
    private static readonly TimeSpan EndpointRetention = TimeSpan.FromDays(14);

    private readonly Lock gate = new();
    private Dictionary<DeviceId, PeerRecord> peers = [];
    private List<string> manualTargets = [];

    /// <summary>Raised after any change to contacts, blocked peers or manual targets.</summary>
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        long cutoff = (timeProvider.GetUtcNow() - EndpointRetention).ToUnixTimeMilliseconds();
        await connection.ExecuteAsync(
            "DELETE FROM PeerEndpoints WHERE COALESCE(LastSuccessAt, LastSeenAt, 0) < @cutoff;", new { cutoff });

        var rows = await connection.QueryAsync<PeerRow>(
            "SELECT DeviceId AS Id, DisplayName, HostName, Alias, TrustLevel AS Level FROM Peers WHERE TrustLevel <> 0;");
        var targets = await connection.QueryAsync<string>("SELECT Target FROM ManualTargets ORDER BY Id;");

        lock (gate)
        {
            peers = rows.Select(r => r.ToRecord()).ToDictionary(r => r.DeviceId);
            manualTargets = [.. targets];
        }
    }

    public TrustLevel GetLevel(DeviceId id)
    {
        lock (gate)
        {
            return peers.TryGetValue(id, out PeerRecord? record) ? record.TrustLevel : TrustLevel.Unknown;
        }
    }

    public PeerRecord? Get(DeviceId id)
    {
        lock (gate)
        {
            return peers.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<PeerRecord> GetAll(TrustLevel level)
    {
        lock (gate)
        {
            return [.. peers.Values.Where(p => p.TrustLevel == level)];
        }
    }

    public IReadOnlyList<string> GetManualTargets()
    {
        lock (gate)
        {
            return [.. manualTargets];
        }
    }

    public Task SetTrustedAsync(DeviceId id, string displayName, string hostName, CancellationToken cancellationToken = default) =>
        SetLevelAsync(id, displayName, hostName, TrustLevel.Trusted, cancellationToken);

    public Task BlockAsync(DeviceId id, string displayName, string hostName, CancellationToken cancellationToken = default) =>
        SetLevelAsync(id, displayName, hostName, TrustLevel.Blocked, cancellationToken);

    /// <summary>Back to Unknown: used for "Ngắt kết nối" and "Bỏ chặn". The alias is forgotten too.</summary>
    public async Task ForgetAsync(DeviceId id, CancellationToken cancellationToken = default)
    {
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("DELETE FROM Peers WHERE DeviceId = @Id;", new { Id = id.Value });
        }

        lock (gate)
        {
            peers.Remove(id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetAliasAsync(DeviceId id, string? alias, CancellationToken cancellationToken = default)
    {
        alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("UPDATE Peers SET Alias = @alias WHERE DeviceId = @Id;", new { alias, Id = id.Value });
        }

        lock (gate)
        {
            if (peers.TryGetValue(id, out PeerRecord? record))
            {
                peers[id] = record with { Alias = alias };
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remembers where a peer was reachable so it can be found again after a restart, even in another subnet.</summary>
    public async Task RecordEndpointAsync(DeviceId id, IPEndPoint endpoint, string source, CancellationToken cancellationToken = default)
    {
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO PeerEndpoints (DeviceId, Address, Port, Source, LastSeenAt, LastSuccessAt)
            VALUES (@Id, @Address, @Port, @source, @now, @now)
            ON CONFLICT (DeviceId, Address, Port) DO UPDATE SET Source = excluded.Source, LastSeenAt = @now, LastSuccessAt = @now;
            """,
            new { Id = id.Value, Address = endpoint.Address.ToString(), endpoint.Port, source, now });
    }

    /// <summary>Known API endpoints of a peer, most recently successful first.</summary>
    public async Task<IReadOnlyList<IPEndPoint>> GetEndpointsAsync(DeviceId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string Address, long Port)>(
            "SELECT Address, Port FROM PeerEndpoints WHERE DeviceId = @Id ORDER BY LastSuccessAt DESC LIMIT 5;",
            new { Id = id.Value });
        return [.. rows.Select(r => new IPEndPoint(IPAddress.Parse(r.Address), (int)r.Port))];
    }

    /// <summary>Last known addresses of all contacts, used to probe contacts outside the local subnet.</summary>
    public async Task<IReadOnlyList<IPAddress>> GetContactAddressesAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(
            """
            SELECT DISTINCT e.Address FROM PeerEndpoints e
            JOIN Peers p ON p.DeviceId = e.DeviceId
            WHERE p.TrustLevel = 1;
            """);
        return [.. rows.Select(IPAddress.Parse)];
    }

    public async Task AddManualTargetAsync(string target, CancellationToken cancellationToken = default)
    {
        target = target.Trim();
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO ManualTargets (Target, Kind) VALUES (@target, 'host') ON CONFLICT (Target) DO NOTHING;", new { target });
        }

        lock (gate)
        {
            if (!manualTargets.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                manualTargets.Add(target);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveManualTargetAsync(string target, CancellationToken cancellationToken = default)
    {
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("DELETE FROM ManualTargets WHERE Target = @target;", new { target });
        }

        lock (gate)
        {
            manualTargets.RemoveAll(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SetLevelAsync(DeviceId id, string displayName, string hostName, TrustLevel level, CancellationToken cancellationToken)
    {
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO Peers (DeviceId, DisplayName, HostName, TrustLevel, TrustedAt)
                VALUES (@Id, @displayName, @hostName, @level, @trustedAt)
                ON CONFLICT (DeviceId) DO UPDATE SET
                  DisplayName = excluded.DisplayName, HostName = excluded.HostName,
                  TrustLevel = excluded.TrustLevel, TrustedAt = excluded.TrustedAt;
                """,
                new { Id = id.Value, displayName, hostName, level = (int)level, trustedAt = level == TrustLevel.Trusted ? now : (long?)null });
        }

        lock (gate)
        {
            string? alias = peers.GetValueOrDefault(id)?.Alias;
            peers[id] = new PeerRecord(id, displayName, hostName, alias, level);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class PeerRow
    {
        public string Id { get; init; } = "";

        public string DisplayName { get; init; } = "";

        public string? HostName { get; init; }

        public string? Alias { get; init; }

        public long Level { get; init; }

        public PeerRecord ToRecord() => new(DeviceId.Parse(Id), DisplayName, HostName ?? "", Alias, (TrustLevel)Level);
    }
}
