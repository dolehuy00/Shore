using System.Globalization;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Shorekeeper.Desktop.Hosting;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Hosting;
using Shorekeeper.Platform.Windows;

namespace Shorekeeper.Desktop;

internal static class Program
{
    /// <summary>Start minimized to the tray (used by the "start with Windows" entry).</summary>
    public const string BackgroundArgument = "--background";

    [STAThread]
    public static int Main(string[] args)
    {
        using SingleInstance instance = SingleInstance.Acquire();
        if (!instance.IsPrimary)
        {
            // Already running in this session: bring it to the front (and later hand over files to send).
            return instance.TrySignalPrimary(args.Length == 0 ? ["--activate"] : args) ? 0 : 1;
        }

        AppPaths paths = AppPaths.ForCurrentUser();
        paths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "shorekeeper-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("Shorekeeper {Version} starting", typeof(Program).Assembly.GetName().Version);

            using IHost host = BuildHost(args, paths);
            host.Start();

            var logger = host.Services.GetRequiredService<ILogger<App>>();
            instance.StartListening(App.OnActivatedByAnotherLaunch, logger);

            App.Services = host.Services;
            App.StartInBackground = args.Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Log.Information("Shorekeeper stopped");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Shorekeeper terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static IHost BuildHost(string[] args, AppPaths paths)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });

        builder.Services.AddSerilog();
        builder.Services.AddShorekeeperEngine(paths);
        builder.Services.AddTransfers();
        builder.Services.AddPeerApi();
        builder.Services.AddMulticastDiscovery();
        builder.Services.AddWindowsPlatform();
        builder.Services.AddSingleton<DialogService>();
        builder.Services.AddSingleton<PeerNames>();
        builder.Services.AddSingleton<InboxViewModel>();
        builder.Services.AddSingleton<SentViewModel>();
        builder.Services.AddSingleton<NeighborhoodViewModel>();
        builder.Services.AddSingleton<NetworkDiagnosticsViewModel>();
        builder.Services.AddSingleton<BlockedPeersViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<GroupsViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();

        return builder.Build();
    }
}
