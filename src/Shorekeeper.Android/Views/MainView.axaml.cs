using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shorekeeper.Android.Platform;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Desktop.Views;
using Button = Avalonia.Controls.Button;
using FeedbackConstants = Android.Views.FeedbackConstants;

namespace Shorekeeper.Android.Views;

/// <summary>Pickers need the window, so the buttons that open one are handled here.</summary>
public partial class MainView : UserControl
{
    private long lastRemeasure;

    /// <summary>The padding Avalonia gave this view (clear of the system bars) while the keyboard lifts it; null otherwise.</summary>
    private Thickness? resting;
    private ChatScroll? chatScroll;

    /// <summary>When a message or card was last held: letting go is not a tap on the picture or link under the finger.</summary>
    private long heldAt;

    /// <summary>Where the finger went down on 🎤, and whether it is still there.</summary>
    private Point micStart;
    private bool micHeld;

    /// <summary>Menus and drop-down lists open now, the newest last, and when one last closed.</summary>
    private static readonly List<Popup> openPopups = [];
    private static long popupClosedAt;

    static MainView()
    {
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, _) =>
        {
            openPopups.Remove(popup);
            if (popup.IsOpen)
            {
                openPopups.Add(popup);
            }
            else
            {
                popupClosedAt = Environment.TickCount64;
            }
        });
        // An open ComboBox focuses its selected item, and on Android its drop-down lives in the same visual tree as the
        // pages: the item's request to be brought into view reached the page's ScrollViewer, which scrolled the page
        // under the drop-down each time one opened. The drop-down is never part of a page, so the page ignores it.
        RequestBringIntoViewEvent.AddClassHandler<ScrollContentPresenter>((_, e) =>
        {
            if (e.TargetObject is Visual target && target.FindAncestorOfType<ComboBoxItem>(includeSelf: true) is not null)
            {
                e.Handled = true;
            }
        });
    }

    public MainView()
    {
        InitializeComponent();
        LayoutLog.View = this;
        SizeChanged += (_, e) => LayoutLog.Event($"size {e.PreviousSize.Width:0}x{e.PreviousSize.Height:0} -> {e.NewSize.Width:0}x{e.NewSize.Height:0}");
        LayoutUpdated += (_, _) => RemeasureIfCollapsed();
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
                // 📎 in a conversation: the same picker as "Gửi file…" on a card.
                main.Chat.PickFiles = () => PickAsync(L.F("Gửi cho {0}", main.Chat.Current?.Name), allowMultiple: true);
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Android's Back closes an open menu or drop-down list, then "Chi tiết" / "Thành viên", then an open
        // conversation, before it leaves the app.
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.BackRequested += (_, back) =>
            {
                if (openPopups.LastOrDefault() is { } popup)
                {
                    popup.Close();
                    back.Handled = true;
                }
                else if (Environment.TickCount64 - popupClosedAt < 250)
                {
                    // The Back key closes the menu itself just before Back gets here (layout.log, 2026-10-30): it is spent.
                    back.Handled = true;
                }
                else if (DataContext is MainViewModel { IsChatTab: true, Chat.HasDetails: true } shown)
                {
                    shown.Chat.CloseDetailsCommand.Execute(null);
                    back.Handled = true;
                }
                else if (DataContext is MainViewModel { IsChatTab: true, Chat.IsOpen: true } main)
                {
                    main.Chat.CloseCommand.Execute(null);
                    back.Handled = true;
                }
            };
            // The keyboard is drawn over the app, which keeps its size: lift the content above it (the message box),
            // and hide the tab strip meanwhile, as other apps do. Avalonia pads this view clear of the system bars; that
            // padding comes back when the keyboard goes, or the tabs would sit under the navigation buttons.
            if (top.InputPane is { } keyboard)
            {
                keyboard.StateChanged += (_, change) =>
                {
                    double covered = change.NewState == InputPaneState.Open ? Math.Max(0, Bounds.Height - change.EndRect.Top) : 0;
                    LayoutLog.Event($"keyboard {change.NewState} {change.EndRect} covers {covered:0}");
                    if (covered > 0)
                    {
                        Thickness rest = resting ??= Padding;
                        Padding = new Thickness(rest.Left, rest.Top, rest.Right, Math.Max(covered, rest.Bottom));
                    }
                    else if (resting is { } restore)
                    {
                        Padding = restore;
                        resting = null;
                    }

                    Tabs.IsVisible = covered == 0;
                };
            }
        }
    }

    /// <summary>
    /// Back from the task switcher with the keyboard gone, Avalonia does not say it closed (layout.log, 2026-10-29): the
    /// content stayed lifted over the keyboard and the tabs hidden. Called on focus when Android says no keyboard shows.
    /// </summary>
    public void KeyboardGone() => Dispatcher.UIThread.Post(() =>
    {
        if (resting is { } restore)
        {
            LayoutLog.Event("keyboard gone without a Closed event, putting the content back");
            Padding = restore;
            resting = null;
            Tabs.IsVisible = true;
        }
    });

    /// <summary>
    /// Rarely, back from the background, the header and the tab strip are 0 high while the window keeps its size, so the
    /// pages slide under the status and navigation bars (layout.log, 2026-10-20: measured against no height while in the
    /// background, then never measured again; turning the phone fixed it). Measures them again when that is seen. Called
    /// after each layout pass, and on resume and focus, which may come with no layout pass at all.
    /// </summary>
    public void RemeasureIfCollapsed() => Dispatcher.UIThread.Post(() =>
    {
        // The header shows in Cài đặt only, the page title elsewhere, the tabs not while typing: only what shows counts.
        bool collapsed = (Header.Parent as Visual)?.IsEffectivelyVisible == true && Bounds.Height > 100
            && ((Header.IsVisible && Header.Bounds.Height <= 0) || (PageTitle.IsVisible && PageTitle.Bounds.Height <= 0)
                || (Tabs.IsVisible && Tabs.Bounds.Height <= 0));
        if (!collapsed || Environment.TickCount64 - lastRemeasure < 2000)
        {
            return;
        }

        lastRemeasure = Environment.TickCount64;
        LayoutLog.Event("collapsed header or tabs, measuring again");
        // Up to the window too: a control measured again on its own reuses its last constraint, the zero height. And
        // everything below: what changed meanwhile (messages that arrived in the background) was measured wrong too.
        foreach (Visual visual in this.GetVisualAncestors().Concat(this.GetSelfAndVisualDescendants()))
        {
            (visual as Layoutable)?.InvalidateMeasure();
        }
    });

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

    /// <summary>Slid this far left, letting go of 🎤 throws the recording away.</summary>
    private const double CancelDistance = 90;

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

    /// <summary>A finger on a card of Hộp nhận or Đã gửi: it starts sinking, as the long press it may become.</summary>
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control card && e.Pointer.Type == PointerType.Touch)
        {
            card.Classes.Remove("released");
            card.Classes.Remove("held");
            card.Classes.Add("pressing");
        }
    }

    /// <summary>Let go (or scrolling took over) before choosing started: the card eases back.</summary>
    private void OnCardLetGo(object? sender, RoutedEventArgs e)
    {
        if (sender is Control card && card.Classes.Remove("pressing"))
        {
            card.Classes.Add("released");
        }
    }

    /// <summary>Long press on a card of Hộp nhận or Đã gửi: choosing starts, with that card checked.</summary>
    private void OnCardHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && sender is Control { DataContext: ISelectableItem item } card)
        {
            card.Classes.Remove("pressing");
            card.Classes.Add("held");
            item.Selection.StartWith(item);
            // The small buzz Android apps give a long press.
            MainActivity.Current?.Window?.DecorView.PerformHapticFeedback(FeedbackConstants.LongPress);
            e.Handled = true;
        }
    }

    /// <summary>
    /// While choosing, a tap anywhere on a card checks or unchecks it; its buttons, the check box among them, still do
    /// their own job.
    /// </summary>
    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ISelectableItem { Selection.IsOn: true } item
            && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    /// <summary>"Gửi file…" on a contact.</summary>
    private async void OnSendFiles(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PeerItemViewModel peer && DataContext is MainViewModel main
            && await PickAsync(L.F("Gửi cho {0}", peer.Name), allowMultiple: true) is { Count: > 0 } picked)
        {
            main.Notice = await peer.SendAsync(picked);
        }
    }

    /// <summary>"Gửi file cho nhóm…": every other member, through the group.</summary>
    private async void OnSendToGroup(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GroupItemViewModel group && DataContext is MainViewModel main
            && await PickAsync(L.F("Gửi cho nhóm {0}", group.Name), allowMultiple: true) is { Count: > 0 } picked)
        {
            main.Notice = await group.SendAsync(picked);
        }
    }

    /// <summary>"Khôi phục…" in Cài đặt: the file first, then its password.</summary>
    private async void OnPickBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main && await PickAsync(L.T("Chọn bản sao lưu Shorekeeper"), allowMultiple: false) is [var file])
        {
            main.Settings.StartRestore(file);
        }
    }

    /// <returns>Paths, or content:// URIs kept exactly as given (their document ids are percent-encoded).</returns>
    private async Task<IReadOnlyList<string>> PickAsync(string title, bool allowMultiple)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return [];
        }

        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = title, AllowMultiple = allowMultiple });
        string[] locations = [.. picked.Select(f => f.Path.IsFile ? f.Path.LocalPath : f.Path.OriginalString)];
        if (MainActivity.Current?.ContentResolver is { } resolver)
        {
            foreach (string location in locations.Where(l => l.StartsWith("content://", StringComparison.Ordinal)))
            {
                ContentUriFiles.Keep(resolver, location);
            }
        }

        return locations;
    }
}
