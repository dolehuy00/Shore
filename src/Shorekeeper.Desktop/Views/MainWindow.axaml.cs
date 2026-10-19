using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shorekeeper.Core.Localization;
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
        // Ctrl+K from anywhere; ↑/↓/Enter/Esc while searching.
        AddHandler(KeyDownEvent, OnSearchShortcut, RoutingStrategies.Tunnel);
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel main)
            {
                main.RevealRequested += (_, item) => Dispatcher.UIThread.Post(() => Reveal(item), DispatcherPriority.Loaded);
                // Pages share the scroller: a new page starts at its top, not where the last one was left.
                main.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.SelectedNavItem))
                    {
                        PageScroller.ScrollToHome();
                    }
                };
            }
        };
    }

    private SearchViewModel? Search => (DataContext as MainWindowViewModel)?.Search;

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

    private void OnSearchShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (Search is not { } search)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                search.Move(1);
                break;
            case Key.Up:
                search.Move(-1);
                break;
            case Key.Enter:
                search.ChooseCommand.Execute(null);
                break;
            case Key.Escape:
                search.Close();
                Focus();
                break;
            default:
                return;
        }

        e.Handled = true;
        if (search.Selected is { } selected)
        {
            SearchResults.ScrollIntoView(selected);
        }
    }

    /// <summary>A click on a result opens it; its quick-action button does its own thing.</summary>
    private void OnSearchResultTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() != true && (sender as Control)?.DataContext is SearchResult result)
        {
            Search?.ChooseCommand.Execute(result);
        }
    }

    /// <summary>Scrolls to the row of a chosen search result and outlines it for a moment.</summary>
    private void Reveal(object item)
    {
        foreach (ItemsControl list in (ItemsControl[])[ContactsList, OthersList, InboxList, SentList, MyGroupsList, NetworkGroupsList])
        {
            if (list.ContainerFromItem(item) is not { } row)
            {
                continue;
            }

            row.BringIntoView();
            if (row.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card") || b.Classes.Contains("panel")) is { } frame)
            {
                frame.Classes.Add("found");
                DispatcherTimer.RunOnce(() => frame.Classes.Remove("found"), TimeSpan.FromSeconds(2));
            }

            return;
        }
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
        // No files: a copied link or paragraph goes as text, shown first so nothing is sent by surprise.
        string? text = paths.Length == 0 ? await clipboard.TryGetTextAsync() : null;
        if (paths.Length == 0 && string.IsNullOrWhiteSpace(text))
        {
            await DialogService.ChooseAsync(L.T("Không có gì để gửi"), L.T("Hãy copy file, thư mục hoặc một đoạn chữ rồi nhấn Ctrl+V."), [L.T("Đóng")]);
        }
        else if (!(paths.Length > 0 ? await main.Neighborhood.SendToSelectedAsync(paths) : await main.Neighborhood.SendTextToSelectedAsync(text!)))
        {
            await DialogService.ChooseAsync(L.T("Chưa chọn người nhận"), L.T("Click vào thẻ một người đã kết nối rồi nhấn Ctrl+V."), [L.T("Đóng")]);
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
