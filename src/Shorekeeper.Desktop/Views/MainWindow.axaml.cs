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
    public MainWindow()
    {
        InitializeComponent();

        // Dropping files on a contact's card sends them (docs/10-ux.md §2).
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

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        PeerCardViewModel? card = CardAt(e.Source);
        ClearDropTargets(except: card);
        bool canDrop = card is { IsTrusted: true } && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        if (card is not null)
        {
            card.IsDropTarget = canDrop;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => ClearDropTargets(except: null);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        PeerCardViewModel? card = CardAt(e.Source);
        ClearDropTargets(except: null);
        if (card is not { IsTrusted: true } || Neighborhood is not { } neighborhood)
        {
            return;
        }

        string[] paths = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()];
        await neighborhood.SendAsync(card, paths);
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

    private void ClearDropTargets(PeerCardViewModel? except)
    {
        foreach (PeerCardViewModel card in Neighborhood?.Contacts ?? [])
        {
            if (card != except)
            {
                card.IsDropTarget = false;
            }
        }
    }
}
