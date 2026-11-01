using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Core.Localization;
using Shorekeeper.UI.ViewModels;
using Shorekeeper.UI.Views;
using Button = Avalonia.Controls.Button;
using FeedbackConstants = Android.Views.FeedbackConstants;

namespace Shorekeeper.Android.Views.Pages;

public partial class ChatPage : UserControl
{
    /// <summary>Slid this far left, letting go of 🎤 throws the recording away.</summary>
    private const double CancelDistance = 90;

    /// <summary>Slid this far right, a message is answered when let go (docs/18-chat-extras.md §4).</summary>
    private const double ReplyDistance = 60;

    /// <summary>Where the finger went down on a message, while it may still slide it right.</summary>
    private Point? slideStart;

    private ChatScroll? chatScroll;

    /// <summary>When a message or card was last held: letting go is not a tap on the picture or link under the finger.</summary>
    private long heldAt;

    /// <summary>Where the finger went down on 🎤, and whether it is still there.</summary>
    private Point micStart;
    private bool micHeld;

    public ChatPage()
    {
        InitializeComponent();
        // A button's command runs only when its Click is not handled.
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (Environment.TickCount64 - heldAt < 1500)
            {
                heldAt = 0;
                e.Handled = true;
            }
        });
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main)
            {
                chatScroll = new ChatScroll(ChatScroller);
                main.Chat.ScrollToEndRequested += (_, _) => chatScroll?.ToEnd();
                main.Chat.RevealRequested += (_, entry) => Dispatcher.UIThread.Post(() => chatScroll?.Reveal(entry), DispatcherPriority.Loaded);
                // 📎 in a conversation: the same picker as "Gửi file…" on a card.
                main.Chat.PickFiles = () => Pickers.PickAsync(this, L.F("Gửi cho {0}", main.Chat.Current?.Name), allowMultiple: true);
            }
        };
    }

    /// <summary>
    /// A long press on a message: its menu (copy, choose several, delete), with the buzz a long press gives; while
    /// choosing, it checks or unchecks the message like a tap.
    /// </summary>
    private void OnBubbleHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || sender is not Control { DataContext: ChatBubbleViewModel message } bubble)
        {
            return;
        }

        MainActivity.Current?.Window?.DecorView.PerformHapticFeedback(FeedbackConstants.LongPress);
        heldAt = Environment.TickCount64;
        if (message.Owner.IsSelecting)
        {
            message.IsSelected = !message.IsSelected;
        }
        else
        {
            FlyoutBase.ShowAttachedFlyout(bubble);
        }

        e.Handled = true;
    }

    /// <summary>A finger down on a message: it may slide it right to answer it.</summary>
    private void OnBubblePressed(object? sender, PointerPressedEventArgs e) => slideStart = e.GetPosition(this);

    /// <summary>
    /// The message follows the finger to the right, up to a little past <see cref="ReplyDistance"/>; a move up or down
    /// first is the list scrolling.
    /// </summary>
    private void OnBubbleMoved(object? sender, PointerEventArgs e)
    {
        if (slideStart is not { } start || sender is not Control { DataContext: ChatBubbleViewModel { CanReply: true, Owner.IsSelecting: false } } bubble)
        {
            return;
        }

        Point now = e.GetPosition(this);
        double right = now.X - start.X;
        if (bubble.RenderTransform is null && (right < 12 || Math.Abs(now.Y - start.Y) > right))
        {
            if (Math.Abs(now.Y - start.Y) > 12)
            {
                slideStart = null;
            }

            return;
        }

        bubble.RenderTransform = new TranslateTransform(Math.Clamp(right, 0, ReplyDistance * 1.3), 0);
        e.Handled = true;
    }

    /// <summary>Let go far enough right: the message is answered, with a buzz; it goes back in place either way.</summary>
    private void OnBubbleReleased(object? sender, PointerReleasedEventArgs e)
    {
        slideStart = null;
        if (sender is Control { DataContext: ChatBubbleViewModel message } bubble && bubble.RenderTransform is TranslateTransform slid)
        {
            bubble.RenderTransform = null;
            if (slid.X >= ReplyDistance)
            {
                MainActivity.Current?.Window?.DecorView.PerformHapticFeedback(FeedbackConstants.VirtualKey);
                message.ReplyCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    /// <summary>The list took the finger to scroll: the message goes back in place.</summary>
    private void OnBubbleLost(object? sender, PointerCaptureLostEventArgs e)
    {
        slideStart = null;
        if (sender is Control bubble)
        {
            bubble.RenderTransform = null;
        }
    }

    /// <summary>
    /// A reaction tapped in the long press menu: the menu closes as after any of its items, once the button's command
    /// has run (closed first, the button would lose the message it reacts to).
    /// </summary>
    private void OnReactionPicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Visual)?.FindAncestorOfType<MenuFlyoutPresenter>() is { } menu)
        {
            Dispatcher.UIThread.Post(menu.Close);
        }
    }

    /// <summary>A long press on a file card: its menu (withdraw what we sent, see it in File).</summary>
    private void OnFileCardHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && sender is Control card)
        {
            MainActivity.Current?.Window?.DecorView.PerformHapticFeedback(FeedbackConstants.LongPress);
            heldAt = Environment.TickCount64;
            FlyoutBase.ShowAttachedFlyout(card);
            e.Handled = true;
        }
    }

    /// <summary>"+N ảnh" / "Thu gọn": the card changes size where it is, its top kept in place.</summary>
    private void OnMorePictures(object? sender, RoutedEventArgs e)
    {
        if (sender is Control button)
        {
            chatScroll?.Hold(button);
        }
    }

    /// <summary>While choosing messages, a tap on a row checks or unchecks it; its buttons and check box do their own job.</summary>
    private void OnChatRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ChatBubbleViewModel { Owner.IsSelecting: true } message }
            && !((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() ?? false))
        {
            message.IsSelected = !message.IsSelected;
            e.Handled = true;
        }
    }

    /// <summary>"@Name" chosen: back in the box, writing on after it.</summary>
    private void OnMentionChosen(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            ChatBox.Focus();
            ChatBox.CaretIndex = ChatBox.Text?.Length ?? 0;
        });

    /// <summary>🎤 held down: recording starts, with the buzz a long press gives.</summary>
    private async void OnMicPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control mic || DataContext is not MainViewModel main)
        {
            return;
        }

        e.Pointer.Capture(mic);
        mic.Classes.Remove("released");
        micStart = e.GetPosition(this);
        micHeld = true;
        if (await main.Chat.StartRecordingAsync())
        {
            MainActivity.Current?.Window?.DecorView.PerformHapticFeedback(FeedbackConstants.LongPress);
            // Let go while the microphone was opening: a tap, not a recording.
            if (!micHeld)
            {
                await main.Chat.FinishRecordingAsync(send: false);
                main.Chat.Notice = L.T("Giữ nút micrô để ghi, thả tay để gửi.");
            }
        }
    }

    private void OnMicMoved(object? sender, PointerEventArgs e)
    {
        if (micHeld && DataContext is MainViewModel { Chat.IsRecording: true } main)
        {
            main.Chat.WillCancel = e.GetPosition(this).X - micStart.X < -CancelDistance;
        }
    }

    private void OnMicReleased(object? sender, PointerReleasedEventArgs e)
    {
        micHeld = false;
        (sender as Control)?.Classes.Add("released");
        if (DataContext is MainViewModel main)
        {
            _ = main.Chat.FinishRecordingAsync(send: !main.Chat.WillCancel);
        }
    }

    /// <summary>The system took the finger away (a dialog, a gesture): nothing is sent.</summary>
    private void OnMicLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (micHeld && DataContext is MainViewModel main)
        {
            micHeld = false;
            (sender as Control)?.Classes.Add("released");
            _ = main.Chat.FinishRecordingAsync(send: false);
        }
    }
}
