using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Shorekeeper.Desktop.ViewModels;

namespace Shorekeeper.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>The card currently lit up under the dragged files.</summary>
    private IFileDropTarget? highlighted;

    public MainWindow()
    {
        InitializeComponent();

        // Dropping files on a person's or a group's card sends them (docs/10-ux.md §2, §6).
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Click selects a card, Ctrl+click several; Ctrl+V sends copied files to them.
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
        KeyDown += OnKeyDown;
    }

    private NeighborhoodViewModel? Neighborhood => (DataContext as MainWindowViewModel)?.Neighborhood;

    private static PeerCardViewModel? CardAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<PeerCardViewModel>().FirstOrDefault();

    private static IFileDropTarget? DropTargetAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<IFileDropTarget>().FirstOrDefault();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        IFileDropTarget? target = DropTargetAt(e.Source);
        bool canDrop = target is { CanDrop: true } && e.DataTransfer.Contains(DataFormat.File);
        Highlight(canDrop ? target : null);
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => Highlight(null);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        IFileDropTarget? target = DropTargetAt(e.Source);
        Highlight(null);
        if (target is not { CanDrop: true })
        {
            return;
        }

        string[] paths = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()];
        await target.DropAsync(paths);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && CardAt(e.Source) is { } card)
        {
            Neighborhood?.Select(card, toggle: e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || DataContext is not MainWindowViewModel { IsNeighborhoodVisible: true } main || Clipboard is not { } clipboard)
        {
            return;
        }

        e.Handled = true;
        string[] paths = [.. (await clipboard.TryGetFilesAsync() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()];
        if (paths.Length == 0)
        {
            await DialogService.ChooseAsync("Không có file để gửi", "Hãy copy file hoặc thư mục (vd trong Explorer) rồi nhấn Ctrl+V.", ["Đóng"]);
        }
        else if (!await main.Neighborhood.SendToSelectedAsync(paths))
        {
            await DialogService.ChooseAsync("Chưa chọn người nhận", "Click vào thẻ một người đã kết nối rồi nhấn Ctrl+V.", ["Đóng"]);
        }
    }

    private void Highlight(IFileDropTarget? target)
    {
        if (highlighted is not null && highlighted != target)
        {
            highlighted.IsDropTarget = false;
        }

        highlighted = target;
        if (target is not null)
        {
            target.IsDropTarget = true;
        }
    }
}
