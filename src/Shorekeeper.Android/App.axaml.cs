using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Android.Platform;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Core.Identity;
using Shorekeeper.Android.Views;
using Shorekeeper.UI.ViewModels;
using Application = Avalonia.Application;

namespace Shorekeeper.Android;

public partial class App : Application
{
    private static MainViewModel? main;
    private static (IReadOnlyList<string> Locations, string? Text)? pendingShare;
    private static DeviceId? pendingChat;
    private static string? pendingGroupChat;

    /// <summary>Set by <see cref="AndroidApp"/> before Avalonia starts.</summary>
    internal static Task<IServiceProvider>? Engine { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single && Engine is { } engine)
        {
            ThemeChoice.Apply(EngineHost.StartTheme);
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
        if (main is null)
        {
            return;
        }

        main.IsInForeground = true;
        if (main.IsInboxTab)
        {
            main.CheckReceivedFiles();
        }
    });

    /// <summary>Out of sight: messages that arrive are not read, and get their notification.</summary>
    internal static void Paused() => Dispatcher.UIThread.Post(() =>
    {
        if (main is not null)
        {
            main.IsInForeground = false;
        }
    });

    /// <summary>A notification was tapped: 0 Xóm, 1 Hộp nhận (File), 4 Âm thanh.</summary>
    internal static void ShowTab(int tab) => Dispatcher.UIThread.Post(() =>
    {
        if (main is not null)
        {
            main.FilesShowSent = false;
            main.SelectedTab = tab;
        }
    });

    /// <summary>A message notification was tapped: its conversation.</summary>
    internal static void ShowChat(string peer) => Dispatcher.UIThread.Post(() =>
    {
        if (DeviceId.TryParse(peer, out DeviceId id))
        {
            if (main is null)
            {
                pendingChat = id;
            }
            else
            {
                main.OpenChat(id);
            }
        }
    });

    /// <summary>A group message notification was tapped: the group's conversation.</summary>
    internal static void ShowGroupChat(string groupId) => Dispatcher.UIThread.Post(() =>
    {
        if (main is null)
        {
            pendingGroupChat = groupId;
        }
        else
        {
            main.OpenGroupChat(groupId);
        }
    });

    private static async Task ShowWhenReadyAsync(Control view, Task<IServiceProvider> engine)
    {
        IServiceProvider services = await engine;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            main = services.GetRequiredService<MainViewModel>();
            main.IsInForeground = true;
            view.DataContext = main;
            ChatNotifications.IsReading = peer => main.Chat.ReadingPeer == peer;
            ChatNotifications.IsReadingGroup = groupId => main.Chat.ReadingGroup == groupId;
            if (pendingChat is { } chat)
            {
                pendingChat = null;
                main.OpenChat(chat);
            }

            if (pendingGroupChat is { } groupChat)
            {
                pendingGroupChat = null;
                main.OpenGroupChat(groupChat);
            }
            if (pendingShare is { } share)
            {
                pendingShare = null;
                main.ShowShare(share.Locations, share.Text);
            }
        });
    }
}
