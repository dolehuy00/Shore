using Avalonia.Controls;

namespace Shorekeeper.Desktop.Views;

/// <summary>
/// Keeps a conversation at its newest message: scrolled there before the next frame is drawn (a long conversation would
/// otherwise show where it was for a frame, then jump), and kept there while pictures load and cards grow, until the
/// person scrolls up to read.
/// </summary>
public sealed class ChatScroll
{
    /// <summary>This close to the end counts as at the end.</summary>
    private const double Slack = 8;

    private readonly ScrollViewer scroller;
    private bool atEnd = true;

    public ChatScroll(ScrollViewer scroller)
    {
        this.scroller = scroller;
        scroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>New messages at the end: lays them out now and shows them.</summary>
    public void ToEnd()
    {
        atEnd = true;
        scroller.UpdateLayout();
        scroller.ScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            // Grown or shrunk under the person (a picture, the keyboard): still at the end if they were.
            if (atEnd)
            {
                scroller.ScrollToEnd();
            }
        }
        else if (e.OffsetDelta.Y != 0)
        {
            atEnd = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - Slack;
        }
    }
}
