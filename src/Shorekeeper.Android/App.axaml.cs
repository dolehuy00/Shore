using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Android.Views;
using Application = Avalonia.Application;

namespace Shorekeeper.Android;

public partial class App : Application
{
    private static MainViewModel? main;
    private static IReadOnlyList<string>? pendingShare;

    /// <summary>Set by <see cref="AndroidApp"/> before Avalonia starts.</summary>
    internal static Task<IServiceProvider>? Engine { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single && Engine is { } engine)
        {
            var view = new MainView();
            single.MainView = view;
            // The engine opens its database and identity in the background; the list fills in when it is ready.
            _ = ShowWhenReadyAsync(view, engine);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Items shared from another app; kept until the engine is ready if the share started the app.</summary>
    internal static void Share(IReadOnlyList<string> locations) => Dispatcher.UIThread.Post(() =>
    {
        if (main is null)
        {
            pendingShare = locations;
        }
        else
        {
            main.ShowShare(locations);
        }
    });

    private static async Task ShowWhenReadyAsync(Control view, Task<IServiceProvider> engine)
    {
        IServiceProvider services = await engine;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            main = new MainViewModel(services);
            view.DataContext = main;
            if (pendingShare is { } locations)
            {
                pendingShare = null;
                main.ShowShare(locations);
            }
        });
    }
}
