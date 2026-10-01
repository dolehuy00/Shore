using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop;

public partial class App : Application
{
    private static readonly Uri IconUri = new("avares://Shorekeeper/Assets/shorekeeper.png");

    private MainWindow? mainWindow;
    private bool isExiting;

    /// <summary>Set by <see cref="Program"/> before Avalonia starts. Null in the designer.</summary>
    public static IServiceProvider? Services { get; set; }

    public static bool StartInBackground { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is not null)
        {
            // Closing the window keeps the app running in the tray; only "Thoát" exits.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            mainWindow = new MainWindow { DataContext = Services.GetRequiredService<MainWindowViewModel>() };
            mainWindow.Closing += OnMainWindowClosing;

            CreateTrayIcon(desktop);

            // Connect requests arrive on API threads; the window must open even when the app is in the tray.
            var pairing = Services.GetRequiredService<PairingService>();
            var dialogs = Services.GetRequiredService<DialogService>();
            pairing.IncomingRequested += (_, request) => Dispatcher.UIThread.Post(() => dialogs.ShowIncoming(request));
            pairing.IncomingClosed += (_, id) => Dispatcher.UIThread.Post(() => dialogs.CloseIncoming(id));

            if (!StartInBackground)
            {
                mainWindow.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Called on a background thread when the user launches Shorekeeper again.</summary>
    internal static void OnActivatedByAnotherLaunch(string[] args) =>
        Dispatcher.UIThread.Post(() => (Current as App)?.ShowMainWindow());

    public void ShowMainWindow()
    {
        if (mainWindow is null)
        {
            return;
        }

        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Activate();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var openItem = new NativeMenuItem("Mở Shorekeeper");
        openItem.Click += (_, _) => ShowMainWindow();

        var exitItem = new NativeMenuItem("Thoát");
        exitItem.Click += (_, _) =>
        {
            isExiting = true;
            desktop.Shutdown();
        };

        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(IconUri)),
            ToolTipText = "Shorekeeper",
            Menu = [openItem, new NativeMenuItemSeparator(), exitItem],
        };
        trayIcon.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(this, [trayIcon]);
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!isExiting)
        {
            e.Cancel = true;
            mainWindow?.Hide();
        }
    }
}
