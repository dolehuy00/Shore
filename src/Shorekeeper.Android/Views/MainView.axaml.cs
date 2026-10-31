using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shorekeeper.Android.Platform;
using Shorekeeper.Android.ViewModels;

namespace Shorekeeper.Android.Views;

/// <summary>The tabs and their pages (Views/Pages), the dialogs over them, and what Back and the keyboard do.</summary>
public partial class MainView : UserControl
{
    private long lastRemeasure;

    /// <summary>The padding Avalonia gave this view (clear of the system bars) while the keyboard lifts it; null otherwise.</summary>
    private Thickness? resting;

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
                else if (DataContext is MainViewModel { IsChatTab: true, Chat.IsSearching: true } searching)
                {
                    searching.Chat.ClearSearchCommand.Execute(null);
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
}
