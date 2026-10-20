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
    private static (IReadOnlyList<string> Locations, string? Text)? pendingShare;

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
    internal static void Share(IReadOnlyList<string> locations, string? text) => Dispatcher.UIThread.Post(() =>
    {
        if (main is null)
        {
            pendingShare = (locations, text);
        }
        else
        {
            main.ShowShare(locations, text);
        }
    });

    /// <summary>Back in the app: received files may have been deleted meanwhile ("Đã xóa file" on Hộp nhận).</summary>
    internal static void Resumed() => Dispatcher.UIThread.Post(() =>
    {
        if (main is { IsInboxTab: true })
        {
            main.CheckReceivedFiles();
        }
    });

    /// <summary>A notification was tapped: 0 Xóm, 1 Hộp nhận, 2 Đã gửi.</summary>
    internal static void ShowTab(int tab) => Dispatcher.UIThread.Post(() =>
    {
        if (main is not null)
        {
            main.SelectedTab = tab;
        }
    });

    private static async Task ShowWhenReadyAsync(Control view, Task<IServiceProvider> engine)
    {
        IServiceProvider services = await engine;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            main = new MainViewModel(services);
            view.DataContext = main;
            if (pendingShare is { } share)
            {
                pendingShare = null;
                main.ShowShare(share.Locations, share.Text);
            }
        });
    }
}
