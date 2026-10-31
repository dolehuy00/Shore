using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Shorekeeper.Desktop.ViewModels;

namespace Shorekeeper.Desktop.Views;

public partial class QuickSendWindow : Window
{
    public QuickSendWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private QuickSendViewModel? ViewModel => DataContext as QuickSendViewModel;

    /// <summary>Set by <see cref="QuickSendService"/>, which opens the window: the pickers of "Thêm file" and "Thêm thư mục".</summary>
    public IDialogs? Dialogs { get; init; }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e) =>
        ViewModel?.Add([.. (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()]);

    private async void OnAddFiles(object? sender, RoutedEventArgs e) => ViewModel?.Add(await Dialogs!.PickFilesAsync(this));

    private async void OnAddFolder(object? sender, RoutedEventArgs e) => ViewModel?.Add(await Dialogs!.PickFolderAsync(this));
}
