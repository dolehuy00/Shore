using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISecretProtector>(new DpapiSecretProtector());
        services.AddSingleton<IPolicyProvider, RegistryPolicyProvider>();
        return services;
    }
}
