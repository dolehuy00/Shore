using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ISecretProtector>(new DpapiSecretProtector());
        services.AddSingleton<IPolicyProvider, RegistryPolicyProvider>();
        services.AddSingleton<IFirewallInspector, WindowsFirewallInspector>();
        services.AddSingleton<IFileTagger, MarkOfTheWebTagger>();
        services.AddSingleton<INotifier, WindowsToastNotifier>();
        services.AddSingleton<IGlobalHotkey, WindowsGlobalHotkey>();
        return services;
    }
}
