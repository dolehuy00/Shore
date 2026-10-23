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
        services.AddSingleton<IAuditSink, EventLogAuditSink>();
        services.AddSingleton<INotifier, WindowsToastNotifier>();
        services.AddSingleton<IGlobalHotkey, WindowsGlobalHotkey>();
        services.AddSingleton<IDirectoryInfo, ActiveDirectoryInfo>();
        services.AddSingleton<IAudioCapture, WindowsAudioCapture>();
        services.AddSingleton<WindowsAudioOutput>();
        services.AddSingleton<IAudioOutput>(sp => sp.GetRequiredService<WindowsAudioOutput>());
        services.AddSingleton<IVoicePlayer>(sp => sp.GetRequiredService<WindowsAudioOutput>());
        services.AddSingleton<IMicrophone, WindowsMicrophone>();
        services.AddSingleton<IMediaKeys, WindowsMediaKeys>();
        services.AddSingleton<IAudioOutputDevices, WindowsAudioOutputDevices>();
        services.AddSingleton<IBluetoothAudioReceiver, WindowsBluetoothAudioReceiver>();
        return services;
    }
}
