using Avalonia.Controls;
using Avalonia.Interactivity;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.Views.Pages;

public partial class GroupsPage : UserControl
{
    public GroupsPage() => InitializeComponent();

    /// <summary>"Gửi file cho nhóm…": every other member, through the group.</summary>
    private async void OnSendToGroup(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GroupItemViewModel group && DataContext is MainViewModel main
            && await Pickers.PickAsync(this, L.F("Gửi cho nhóm {0}", group.Name), allowMultiple: true) is { Count: > 0 } picked)
        {
            main.Notice = await group.SendAsync(picked);
        }
    }
}
