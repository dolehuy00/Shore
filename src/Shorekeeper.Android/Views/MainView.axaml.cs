using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shorekeeper.Android.Platform;
using Shorekeeper.Android.ViewModels;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.Views;

/// <summary>Pickers need the window, so the buttons that open one are handled here.</summary>
public partial class MainView : UserControl
{
    private long lastRemeasure;

    static MainView()
    {
        // An open ComboBox focuses its selected item, and on Android its drop-down lives in the same visual tree as the
        // pages: the item's request to be brought into view reached the page's ScrollViewer, which scrolled the page
        // under the drop-down each time one opened. The drop-down is never part of a page, so the page ignores it.
        RequestBringIntoViewEvent.AddClassHandler<ScrollContentPresenter>((_, e) =>
        {
            if (e.TargetObject is Visual target && target.FindAncestorOfType<ComboBoxItem>(includeSelf: true) is not null)
            {
                e.Handled = true;
            }
        });
    }

    public MainView()
    {
        InitializeComponent();
        LayoutLog.View = this;
        SizeChanged += (_, e) => LayoutLog.Event($"size {e.PreviousSize.Width:0}x{e.PreviousSize.Height:0} -> {e.NewSize.Width:0}x{e.NewSize.Height:0}");
        LayoutUpdated += (_, _) => RemeasureIfCollapsed();
    }

    /// <summary>
    /// Rarely, back from the background, the header and the tab strip are 0 high while the window keeps its size, so the
    /// pages slide under the status and navigation bars (layout.log, 2026-10-20: measured against no height while in the
    /// background, then never measured again; turning the phone fixed it). Measures them again when that is seen. Called
    /// after each layout pass, and on resume and focus, which may come with no layout pass at all.
    /// </summary>
    public void RemeasureIfCollapsed() => Dispatcher.UIThread.Post(() =>
    {
        bool collapsed = Header.IsEffectivelyVisible && Bounds.Height > 100 && (Header.Bounds.Height <= 0 || Tabs.Bounds.Height <= 0);
        if (!collapsed || Environment.TickCount64 - lastRemeasure < 2000)
        {
            return;
        }

        lastRemeasure = Environment.TickCount64;
        LayoutLog.Event("collapsed header or tabs, measuring again");
        // Up to the window too: a control measured again on its own reuses its last constraint, the zero height.
        foreach (Visual visual in Header.GetSelfAndVisualAncestors().Concat(Header.GetVisualDescendants()).Concat(Tabs.GetSelfAndVisualDescendants()))
        {
            (visual as Layoutable)?.InvalidateMeasure();
        }
    });

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
