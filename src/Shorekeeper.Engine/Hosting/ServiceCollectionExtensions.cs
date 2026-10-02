using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Hosting;

/// <summary>
/// Hosted services start in registration order. Call <see cref="AddShorekeeperEngine"/>, then
/// <see cref="AddTransfers"/>, <see cref="AddPeerApi"/> and <see cref="AddMulticastDiscovery"/>:
/// transfers load before the API serves them, and discovery announces the API port.
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
        services.AddSingleton<GroupStore>();
        services.AddSingleton<PeerDirectory>();
        services.AddSingleton<LocalDevice>();
        services.AddSingleton<ShorekeeperEngine>();
        services.AddHostedService<EngineHostedService>();

        return services;
    }

    /// <summary>Registers sending and receiving files.</summary>
    public static IServiceCollection AddTransfers(this IServiceCollection services)
    {
        services.TryAddSingleton<IFileTagger>(NullFileTagger.Instance);
        services.AddSingleton<FileHasher>();
        services.AddSingleton<FileDownloader>();
        services.AddSingleton<OfferService>();
        services.AddSingleton<InboxService>();
        services.AddHostedService<TransferWorker>();

        return services;
    }

    /// <summary>
    /// Registers the HTTPS peer API (mutual TLS), the client for calling peers, connecting,
    /// what runs over the API to see other subnets (PEX, the Bridge role), and groups.
    /// </summary>
    public static IServiceCollection AddPeerApi(this IServiceCollection services)
    {
        services.TryAddSingleton(new ApiServerOptions());
        services.AddSingleton<ApiServer>();
        services.AddHostedService(sp => sp.GetRequiredService<ApiServer>());
        services.AddSingleton<PeerClient>();
        services.AddSingleton<PairingService>();

        services.AddSingleton<LocalPresence>();
        services.AddSingleton<ILocalPresence>(sp => sp.GetRequiredService<LocalPresence>());
        services.AddSingleton<ProbeHints>();
        services.AddSingleton<BridgeRegistry>();
        services.AddSingleton<BridgeClient>();
        services.AddHostedService(sp => sp.GetRequiredService<BridgeClient>());
        services.AddSingleton<PexService>();
        services.AddHostedService(sp => sp.GetRequiredService<PexService>());
        services.AddSingleton<GroupService>();
        services.AddHostedService(sp => sp.GetRequiredService<GroupService>());

        return services;
    }

    /// <summary>Registers UDP discovery: multicast in the subnet, unicast probes beyond it, Subnet Probe.</summary>
    public static IServiceCollection AddMulticastDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<IProbeTargetSource, ProbeTargets>();
        services.AddSingleton<ManualPeerFinder>();
        services.AddHostedService<MulticastDiscovery>();
        services.AddHostedService<SubnetScanner>();

        return services;
    }
}
