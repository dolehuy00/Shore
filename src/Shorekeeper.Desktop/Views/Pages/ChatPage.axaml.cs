using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.UI.ViewModels;
using Shorekeeper.UI.Views;

namespace Shorekeeper.Desktop.Views.Pages;

public partial class ChatPage : UserControl
{
    private ChatScroll? chatScroll;

    public ChatPage()
    {
        InitializeComponent();

        // Enter sends a message, Shift+Enter starts a new line.
        ChatBox.AddHandler(KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel main)
            {
                chatScroll = new ChatScroll(ChatScroller);
                main.Chat.ScrollToEndRequested += (_, _) => chatScroll?.ToEnd();
                main.Chat.RevealRequested += (_, entry) => Dispatcher.UIThread.Post(() => chatScroll?.Reveal(entry), DispatcherPriority.Loaded);
            }
        };
    }

    /// <summary>"+N ảnh" / "Thu gọn": the card changes size where it is, its top kept in place.</summary>
    private void OnMorePictures(object? sender, RoutedEventArgs e)
    {
        if (sender is Control button)
        {
            chatScroll?.Hold(button);
        }
    }

    /// <summary>While choosing messages, a click on a row checks or unchecks it; its buttons and check box do their own job.</summary>
    private void OnChatRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ChatBubbleViewModel { Owner.IsSelecting: true } bubble }
            && !((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() ?? false))
        {
            bubble.IsSelected = !bubble.IsSelected;
            e.Handled = true;
        }
    }

    private void OnChatKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+V: copied files, or a picture copied as a picture, go to this person; text is pasted as usual.
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainWindowViewModel { Chat.CanAttach: true } chat
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            e.Handled = true;
            _ = PasteIntoChatAsync(chat, clipboard);
            return;
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && DataContext is MainWindowViewModel main)
        {
            e.Handled = true;
            if (main.Chat.SendCommand.CanExecute(null))
            {
                main.Chat.SendCommand.Execute(null);
            }
        }
    }

    private async Task PasteIntoChatAsync(MainWindowViewModel main, IClipboard clipboard)
    {
        string[] paths = [.. (await clipboard.TryGetFilesAsync() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()];
        if (paths.Length == 0 && !string.IsNullOrEmpty(await clipboard.TryGetTextAsync()))
        {
            ChatBox.Paste();
            return;
        }

        if (paths.Length == 0 && await ClipboardImage.SaveAsync(clipboard, main.ClipboardDirectory) is { } picture)
        {
            paths = [picture];
        }

        await main.Chat.AttachAsync(paths);
    }
}
