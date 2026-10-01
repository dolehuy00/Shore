using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

public sealed class TrustStoreTests : IAsyncLifetime
{
    private static readonly DeviceId Mai = DeviceId.FromSubjectPublicKeyInfo([1]);
    private static readonly DeviceId Tuan = DeviceId.FromSubjectPublicKeyInfo([2]);

    private readonly TempAppFolder folder = new();
    private SqliteDatabase database = null!;

    public async ValueTask InitializeAsync()
    {
        database = new SqliteDatabase(folder.Paths);
        await new MigrationRunner(database, NullLogger<MigrationRunner>.Instance).MigrateAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        folder.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Contacts_blocks_and_aliases_survive_a_restart()
    {
        TrustStore store = await LoadAsync();
        await store.SetTrustedAsync(Mai, "Mai", "PC-QA-07", Ct);
        await store.SetAliasAsync(Mai, "Mai bên QA", Ct);
        await store.BlockAsync(Tuan, "Tuấn", "PC-DEV-11", Ct);

        TrustStore reloaded = await LoadAsync();

        Assert.Equal(TrustLevel.Trusted, reloaded.GetLevel(Mai));
        Assert.Equal("Mai bên QA", reloaded.Get(Mai)!.ShownName);
        Assert.Equal(TrustLevel.Blocked, reloaded.GetLevel(Tuan));
        Assert.Equal(TrustLevel.Unknown, reloaded.GetLevel(DeviceId.FromSubjectPublicKeyInfo([3])));
    }

    [Fact]
    public async Task Forget_returns_peer_to_unknown_and_drops_alias()
    {
        TrustStore store = await LoadAsync();
        await store.SetTrustedAsync(Mai, "Mai", "PC-QA-07", Ct);
        await store.SetAliasAsync(Mai, "Mai bên QA", Ct);

        await store.ForgetAsync(Mai, Ct);
        await store.SetTrustedAsync(Mai, "Mai", "PC-QA-07", Ct);

        Assert.Equal("Mai", (await LoadAsync()).Get(Mai)!.ShownName);
    }

    [Fact]
    public async Task Endpoints_are_returned_most_recent_first_and_only_for_contacts_in_probe_list()
    {
        var time = new ManualTimeProvider();
        TrustStore store = await LoadAsync(time);
        await store.SetTrustedAsync(Mai, "Mai", "PC-QA-07", Ct);
        await store.RecordEndpointAsync(Mai, new IPEndPoint(IPAddress.Parse("10.1.5.12"), 47471), "manual", Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await store.RecordEndpointAsync(Mai, new IPEndPoint(IPAddress.Parse("10.1.5.20"), 47471), "known", Ct);
        await store.RecordEndpointAsync(Tuan, new IPEndPoint(IPAddress.Parse("10.1.6.3"), 47471), "known", Ct);

        var endpoints = await store.GetEndpointsAsync(Mai, Ct);
        var contactAddresses = await store.GetContactAddressesAsync(Ct);

        Assert.Equal(["10.1.5.20", "10.1.5.12"], endpoints.Select(e => e.Address.ToString()));
        Assert.DoesNotContain(IPAddress.Parse("10.1.6.3"), contactAddresses);
        Assert.Equal(2, contactAddresses.Count);
    }

    [Fact]
    public async Task Old_endpoints_are_dropped_on_load()
    {
        var time = new ManualTimeProvider();
        TrustStore store = await LoadAsync(time);
        await store.RecordEndpointAsync(Mai, new IPEndPoint(IPAddress.Parse("10.1.5.12"), 47471), "known", Ct);

        time.Advance(TimeSpan.FromDays(15));
        TrustStore reloaded = await LoadAsync(time);

        Assert.Empty(await reloaded.GetEndpointsAsync(Mai, Ct));
    }

    [Fact]
    public async Task Manual_targets_are_kept_once()
    {
        TrustStore store = await LoadAsync();
        await store.AddManualTargetAsync("PC-DEV-11", Ct);
        await store.AddManualTargetAsync(" PC-DEV-11 ", Ct);
        await store.AddManualTargetAsync("10.1.5.12:47471", Ct);

        Assert.Equal(["PC-DEV-11", "10.1.5.12:47471"], (await LoadAsync()).GetManualTargets());
    }

    [Fact]
    public async Task Manual_target_can_be_removed()
    {
        TrustStore store = await LoadAsync();
        await store.AddManualTargetAsync("PC-DEV-11", Ct);

        await store.RemoveManualTargetAsync("PC-DEV-11", Ct);

        Assert.Empty(store.GetManualTargets());
        Assert.Empty((await LoadAsync()).GetManualTargets());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TrustStore> LoadAsync(TimeProvider? time = null)
    {
        var store = new TrustStore(database, time ?? TimeProvider.System);
        await store.LoadAsync(Ct);
        return store;
    }
}
