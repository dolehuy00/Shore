using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Hosting;

/// <summary>
/// Hosted services start in registration order. Call <see cref="AddShorekeeperEngine"/>, then
/// <see cref="AddPeerApi"/>, then <see cref="AddMulticastDiscovery"/>: discovery announces the API port.
/// </summary>
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
        services.TryAddSingleton<IFirewallInspector>(NullFirewallInspector.Instance);

        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<IdentityStore>();
        services.AddSingleton<TrustStore>();
        services.AddSingleton<PeerDirectory>();
        services.AddSingleton<LocalDevice>();
        services.AddSingleton<ShorekeeperEngine>();
        services.AddHostedService<EngineHostedService>();

        return services;
    }

    /// <summary>Registers the HTTPS peer API (mutual TLS), the client for calling peers, and connecting.</summary>
    public static IServiceCollection AddPeerApi(this IServiceCollection services)
    {
        services.TryAddSingleton(new ApiServerOptions());
        services.AddSingleton<ApiServer>();
        services.AddHostedService(sp => sp.GetRequiredService<ApiServer>());
        services.AddSingleton<PeerClient>();
        services.AddSingleton<PairingService>();

        return services;
    }

    /// <summary>Registers UDP discovery: multicast in the subnet, unicast probes beyond it.</summary>
    public static IServiceCollection AddMulticastDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<LocalPresence>();
        services.AddSingleton<ILocalPresence>(sp => sp.GetRequiredService<LocalPresence>());
        services.AddSingleton<IProbeTargetSource, ProbeTargets>();
        services.AddSingleton<ManualPeerFinder>();
        services.AddHostedService<MulticastDiscovery>();

        return services;
    }
}
