using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
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
using Button = Avalonia.Controls.Button;
using FeedbackConstants = Android.Views.FeedbackConstants;

namespace Shorekeeper.Android.Views;

/// <summary>Pickers need the window, so the buttons that open one are handled here.</summary>
public partial class MainView : UserControl
{
    private long lastRemeasure;

    /// <summary>Where the finger went down on 🎤, and whether it is still there.</summary>
    private Point micStart;
    private bool micHeld;

    static MainView()
    {
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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main)
            {
                // After the new bubbles are laid out, so the end is where they are.
                main.Chat.ScrollToEndRequested += (_, _) => Dispatcher.UIThread.Post(() => ChatScroller.ScrollToEnd(), DispatcherPriority.Loaded);
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Android's Back closes an open conversation before it leaves the app.
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.BackRequested += (_, back) =>
            {
                if (DataContext is MainViewModel { IsChatTab: true, Chat.IsOpen: true } main)
                {
                    main.Chat.CloseCommand.Execute(null);
                    back.Handled = true;
                }
            };
            // The keyboard is drawn over the app, which keeps its size: lift the content above it (the message box),
            // and hide the tab strip meanwhile, as other apps do.
            if (top.InputPane is { } keyboard)
            {
                keyboard.StateChanged += (_, change) =>
                {
                    double covered = change.NewState == InputPaneState.Open ? Math.Max(0, Bounds.Height - change.EndRect.Top) : 0;
                    LayoutLog.Event($"keyboard {change.NewState} {change.EndRect} covers {covered:0}");
                    Padding = new Thickness(0, 0, 0, covered);
                    Tabs.IsVisible = covered == 0;
                };
            }
        }
    }

    /// <summary>
    /// Rarely, back from the background, the header and the tab strip are 0 high while the window keeps its size, so the
    /// pages slide under the status and navigation bars (layout.log, 2026-10-20: measured against no height while in the
    /// background, then never measured again; turning the phone fixed it). Measures them again when that is seen. Called
    /// after each layout pass, and on resume and focus, which may come with no layout pass at all.
    /// </summary>
    public void RemeasureIfCollapsed() => Dispatcher.UIThread.Post(() =>
    {
        bool collapsed = Header.IsEffectivelyVisible && Bounds.Height > 100 && (Header.Bounds.Height <= 0 || Tabs.Bounds.Height <= 0);
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
        return [.. picked.Select(f => f.Path.IsFile ? f.Path.LocalPath : f.Path.OriginalString)];
    }
}
