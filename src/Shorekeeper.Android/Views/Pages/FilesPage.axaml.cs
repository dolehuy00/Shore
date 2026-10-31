using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Shorekeeper.Android.ViewModels;
using Button = Avalonia.Controls.Button;
using FeedbackConstants = Android.Views.FeedbackConstants;

namespace Shorekeeper.Android.Views.Pages;

public partial class FilesPage : UserControl
{
    public FilesPage() => InitializeComponent();

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
}
