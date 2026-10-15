using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Shorekeeper.Android.ViewModels;

namespace Shorekeeper.Android.Views;

public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();

    /// <summary>"Gửi file…" on a contact: Android's file picker, then send what was picked (content:// URIs).</summary>
    private async void OnSendFiles(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PeerItemViewModel peer || DataContext is not MainViewModel main
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = $"Gửi cho {peer.Name}", AllowMultiple = true });
        if (picked.Count > 0)
        {
            // Keep content:// URIs exactly as given: their document ids are percent-encoded.
            main.Notice = await peer.SendAsync([.. picked.Select(f => f.Path.IsFile ? f.Path.LocalPath : f.Path.OriginalString)]);
        }
    }
}
