using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Shorekeeper.Desktop.Views;

/// <summary>
/// Keeps a conversation at its newest message: scrolled there before the next frame is drawn (a long conversation would
/// otherwise show where it was for a frame, then jump), and kept there while pictures load and cards grow, until the
/// person scrolls up to read. A card opened or closed with "+N ảnh" stays where it is instead.
/// </summary>
public sealed class ChatScroll
{
    /// <summary>This close to the end counts as at the end.</summary>
    private const double Slack = 8;

    private readonly ScrollViewer scroller;
    private bool atEnd = true;

    /// <summary>The message list and the entry in it kept in place, and where its bottom is in the viewport.</summary>
    private ItemsControl? list;
    private object? held;
    private double heldBottom;

    /// <summary>Where we last scrolled to keep it, so that is not taken for the person scrolling.</summary>
    private double heldOffset = double.NaN;

    /// <summary>Laying the entry out again scrolls too; that is handled by the call that asked for it.</summary>
    private bool keeping;

    public ChatScroll(ScrollViewer scroller)
    {
        this.scroller = scroller;
        scroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>New messages at the end: lays them out now and shows them.</summary>
    public void ToEnd()
    {
        atEnd = true;
        held = null;
        scroller.UpdateLayout();
        scroller.ScrollToEnd();
    }

    /// <summary>
    /// The entry of <paramref name="control"/> is about to change size ("+N ảnh"): its bottom, where the finger is, stays
    /// where it is on screen until the person scrolls, rather than following the end. Kept by the item, not its control:
    /// a virtualizing list may lay the entry out again elsewhere, or not at all, once its size changes.
    /// </summary>
    public void Hold(Control control)
    {
        if (control.FindAncestorOfType<ItemsControl>() is { } items && control.DataContext is { } item && Bottom(items, item) is { } bottom)
        {
            list = items;
            held = item;
            heldBottom = bottom;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            if (held is not null)
            {
                KeepHeld();
            }
            else if (atEnd)
            {
                // Grown or shrunk under the person (a picture, the keyboard): still at the end if they were.
                scroller.ScrollToEnd();
            }
        }
        else if (e.OffsetDelta.Y != 0 && !(held is not null && Math.Abs(scroller.Offset.Y - heldOffset) < 0.5))
        {
            held = null;
            atEnd = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - Slack;
        }
    }

    private void KeepHeld()
    {
        if (keeping)
        {
            return;
        }

        keeping = true;
        try
        {
            double? bottom = Bottom(list!, held!);
            if (bottom is null)
            {
                list!.ScrollIntoView(held!);
                bottom = Bottom(list, held!);
            }

            if (bottom is { } at && Math.Abs(at - heldBottom) >= 0.5)
            {
                scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + at - heldBottom);
                heldOffset = scroller.Offset.Y;
            }
        }
        finally
        {
            keeping = false;
        }
    }

    /// <summary>Where the entry of <paramref name="item"/> ends in the viewport, if it is laid out.</summary>
    private double? Bottom(ItemsControl items, object item) =>
        items.ContainerFromItem(item) is { IsArrangeValid: true } container
            ? container.TranslatePoint(new Point(0, container.Bounds.Height), scroller)?.Y
            : null;
}
