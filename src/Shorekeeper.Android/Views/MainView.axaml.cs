using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.Views;

/// <summary>Pickers need the window, so the buttons that open one are handled here.</summary>
public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();

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
