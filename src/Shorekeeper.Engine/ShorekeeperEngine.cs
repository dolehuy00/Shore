using Microsoft.Extensions.Logging;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine;

/// <summary>
/// Entry point of the engine shared by the desktop app (and headless test hosts).
/// Prepares folders, settings, database schema, contacts and the device identity.
/// </summary>
public sealed class ShorekeeperEngine(
    AppPaths paths,
    SettingsService settings,
    MigrationRunner migrations,
    IdentityStore identityStore,
    TrustStore trustStore,
    ILogger<ShorekeeperEngine> logger) : IDisposable
{
    private DeviceIdentity? identity;

    public bool IsInitialized => identity is not null;

    public DeviceIdentity Identity => identity ?? throw new InvalidOperationException("Engine has not been initialized.");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (identity is not null)
        {
            return;
        }

        paths.EnsureCreated();
        settings.Load();
        await migrations.MigrateAsync(cancellationToken);
        await trustStore.LoadAsync(cancellationToken);
        identity = identityStore.LoadOrCreate();

        logger.LogInformation(
            "Engine ready: {DisplayName} ({DeviceId}), data in {DataDirectory}",
            settings.Current.DisplayName,
            identity.DeviceId,
            paths.DataDirectory);
    }

    public void Dispose() => identity?.Dispose();
}
