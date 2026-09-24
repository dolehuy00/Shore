using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Storage;

namespace Shorekeeper.Engine.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine. The caller must also register an <see cref="ISecretProtector"/>
    /// (platform-specific); <see cref="IPolicyProvider"/> defaults to no policies.
    /// </summary>
    public static IServiceCollection AddShorekeeperEngine(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPolicyProvider>(NullPolicyProvider.Instance);

        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<IdentityStore>();
        services.AddSingleton<ShorekeeperEngine>();
        services.AddHostedService<EngineHostedService>();

        return services;
    }
}
