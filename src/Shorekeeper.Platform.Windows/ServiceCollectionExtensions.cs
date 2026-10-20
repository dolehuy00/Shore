using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Platform;
using Shorekeeper.Platform.Windows.Audio;

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
        services.AddSingleton<IDirectoryInfo, ActiveDirectoryInfo>();
        services.AddSingleton<IAudioCapture, WindowsAudioCapture>();
        services.AddSingleton<IAudioOutput, WindowsAudioOutput>();
        services.AddSingleton<IMediaKeys, WindowsMediaKeys>();
        services.AddSingleton<IBluetoothAudioReceiver, WindowsBluetoothAudioReceiver>();
        return services;
    }
}
