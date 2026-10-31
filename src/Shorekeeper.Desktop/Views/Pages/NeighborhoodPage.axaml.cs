using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Shorekeeper.Desktop.Views.Pages;

public partial class NeighborhoodPage : UserControl
{
    public NeighborhoodPage() => InitializeComponent();

    /// <summary>"…" on a person: the same menu as a right-click on the row.</summary>
    private void OnPeerMore(object? sender, RoutedEventArgs e)
    {
        // A context menu only opens on the control it belongs to; it shows at the pointer, which is on the button.
        if (sender is Control button
            && button.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.ContextMenu is not null) is { ContextMenu: { } menu } row)
        {
            menu.Open(row);
        }
    }
}
