using Avalonia.Controls;
using Avalonia.Interactivity;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    /// <summary>"Khôi phục…": the file first, then its password.</summary>
    private async void OnPickBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main && await Pickers.PickAsync(this, L.T("Chọn bản sao lưu Shorekeeper"), allowMultiple: false) is [var file])
        {
            main.Settings.StartRestore(file);
        }
    }
}
