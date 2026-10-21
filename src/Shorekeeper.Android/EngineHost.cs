using Android.Content;
using Android.Net.Wifi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Android.Platform;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Audio;
using Shorekeeper.Engine.Hosting;
using Shorekeeper.Engine.Settings;
using AndroidEnvironment = Android.OS.Environment;
using Build = Android.OS.Build;
using GlobalSettings = Android.Provider.Settings.Global;

namespace Shorekeeper.Android;

/// <summary>
/// The engine inside the Android app: same services as the desktop (peer API on <see cref="Engine.Api.MiniHttpServer"/>),
/// data in the app's private storage, received files in Download/Shorekeeper.
/// </summary>
public static class EngineHost
{
    private static Task<IServiceProvider>? starting;
    private static WifiManager.MulticastLock? multicastLock;

    /// <summary>Started once per process; later calls get the same task.</summary>
    public static Task<IServiceProvider> StartAsync(Context context)
    {
        if (starting is null)
        {
            var paths = new AppPaths(
                Path.Combine(context.FilesDir!.AbsolutePath, "Shorekeeper"),
                Path.Combine(DownloadDirectory(), "Shorekeeper"));
            paths.EnsureCreated();

            // The UI is built while the engine starts, so the language chosen in Settings must be in place first.
            L.Use(new SettingsStore(paths, TimeProvider.System, NullLogger<SettingsStore>.Instance).Load().Language);
            starting = Task.Run(() => StartCoreAsync(context, paths));
        }

        return starting;
    }

    private static async Task<IServiceProvider> StartCoreAsync(Context context, AppPaths paths)
    {
        // Machine and user names mean nothing on a phone ("localhost", "u0_a280").
        string name = GlobalSettings.GetString(context.ContentResolver, "device_name") ?? Build.Model ?? "Android";
        LocalDevice.HostName = name;
        SettingsService.DefaultDisplayName = name;

        // Without this lock Android drops the presence packets other devices multicast.
        var wifi = (WifiManager)context.GetSystemService(Context.WifiService)!;
        multicastLock = wifi.CreateMulticastLock("shorekeeper-discovery")!;
        multicastLock.SetReferenceCounted(false);
        multicastLock.Acquire();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.AddProvider(new LogcatLoggerProvider());
        builder.Services.AddSingleton<ISecretProtector, KeystoreSecretProtector>();
        builder.Services.AddSingleton<IExternalFileSource>(new ContentUriFiles(context.ContentResolver!));
        builder.Services.AddSingleton<IAudioOutput>(new AndroidAudioOutput(context.ApplicationContext ?? context));
        var capture = new AndroidAudioCapture(context.ApplicationContext ?? context);
        builder.Services.AddSingleton<IAudioCapture>(capture);
        builder.Services.AddSingleton<IMicrophone>(capture);
        builder.Services.AddSingleton<IVoicePlayer>(new AndroidVoicePlayer());
        builder.Services.AddSingleton<IMediaLibrary>(new MediaStoreLibrary(context.ApplicationContext ?? context));
        builder.Services.AddShorekeeperEngine(paths);
        builder.Services.AddTransfers();
        builder.Services.AddPeerApi();
        builder.Services.AddMulticastDiscovery();

        IHost host = builder.Build();
        await host.StartAsync();
        BackgroundTransfers.Start(context.ApplicationContext ?? context, host.Services);
        HomeWidget.Watch(context.ApplicationContext ?? context, host.Services);
        ChatNotifications.Start(context.ApplicationContext ?? context, host.Services);

        // "Phát âm thanh" and the projection it runs on end together, whichever side ends first.
        var audio = host.Services.GetRequiredService<AudioService>();
        PlaybackCapture.Stopped += () => audio.SetPlaying(false);
        audio.Changed += (_, _) =>
        {
            if (!audio.IsPlaying)
            {
                PlaybackCapture.Release(context.ApplicationContext ?? context);
            }
        };
        return host.Services;
    }

    /// <summary>The shared Download folder; apps may create their own files there by path again since Android 11.</summary>
    private static string DownloadDirectory()
    {
#pragma warning disable CA1422 // Deprecated in API 29 in favour of MediaStore, but the path is still the right place to write to.
        return AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDownloads)!.AbsolutePath;
#pragma warning restore CA1422
    }
}
