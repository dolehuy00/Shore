using Avalonia.Controls;
using Avalonia.Interactivity;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.Views.Pages;

public partial class NearbyPage : UserControl
{
    public NearbyPage() => InitializeComponent();

    /// <summary>"Gửi file…" on a contact.</summary>
    private async void OnSendFiles(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PeerItemViewModel peer && DataContext is MainViewModel main
            && await Pickers.PickAsync(this, L.F("Gửi cho {0}", peer.Name), allowMultiple: true) is { Count: > 0 } picked)
        {
            main.Notice = await peer.SendAsync(picked);
        }
    }
}
