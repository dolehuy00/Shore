using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Shorekeeper.UI.ViewModels;

namespace Shorekeeper.Desktop.Views;

public partial class PictureWindow : Window
{
    /// <summary>Following the window's size until the wheel zooms.</summary>
    private bool fitted = true;
    private double scale = 1;

    /// <summary>Where a drag started, and where the picture was then.</summary>
    private Point? dragFrom;
    private Vector dragOffset;

    public PictureWindow()
    {
        InitializeComponent();
        KeyDown += OnKey;
        Scroller.SizeChanged += (_, _) =>
        {
            if (fitted)
            {
                Fit();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (Viewer is { } viewer)
            {
                viewer.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PictureViewerViewModel.Image))
                    {
                        Fit();
                    }
                };
            }
        };
        // The pictures are let go once nothing shows them.
        Closed += (_, _) => Viewer?.Dispose();
    }

    private PictureViewerViewModel? Viewer => DataContext as PictureViewerViewModel;

    /// <summary>The whole picture in the window, never bigger than it is.</summary>
    private void Fit()
    {
        fitted = true;
        if (Viewer?.Image is not { } image || image.Size.Width <= 0 || image.Size.Height <= 0)
        {
            return;
        }

        Size view = Scroller.Bounds.Size;
        Zoom(Math.Min(1, Math.Min(view.Width / image.Size.Width, view.Height / image.Size.Height)));
    }

    private void Zoom(double to)
    {
        scale = Math.Clamp(to, 0.05, 8);
        Zoomed.LayoutTransform = new ScaleTransform(scale, scale);
    }

    /// <summary>The wheel zooms around the pointer: the point under it stays there.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        Point at = e.GetPosition(Scroller);
        Vector offset = Scroller.Offset;
        double before = scale;
        Zoom(scale * Math.Pow(1.2, e.Delta.Y));
        fitted = false;
        Scroller.UpdateLayout();
        double grown = scale / before;
        Scroller.Offset = new Vector(((offset.X + at.X) * grown) - at.X, ((offset.Y + at.Y) * grown) - at.Y);
        e.Handled = true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Scroller).Properties.IsLeftButtonPressed)
        {
            dragFrom = e.GetPosition(Scroller);
            dragOffset = Scroller.Offset;
            e.Pointer.Capture(Scroller);
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (dragFrom is { } from)
        {
            Point now = e.GetPosition(Scroller);
            Scroller.Offset = dragOffset - (now - from);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        dragFrom = null;
        e.Pointer.Capture(null);
    }

    /// <summary>←/→ the others, + / − zoom, 0 fits, Esc closes.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                Viewer?.PreviousCommand.Execute(null);
                break;
            case Key.Right:
                Viewer?.NextCommand.Execute(null);
                break;
            case Key.Add or Key.OemPlus:
                fitted = false;
                Zoom(scale * 1.2);
                break;
            case Key.Subtract or Key.OemMinus:
                fitted = false;
                Zoom(scale / 1.2);
                break;
            case Key.D0 or Key.NumPad0:
                Fit();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Viewer?.Location is { } path)
        {
            _ = Task.Run(() => ClipboardImage.Copy(path));
        }
    }

    private void OnShowInFolder(object? sender, RoutedEventArgs e)
    {
        if (Viewer?.Location is { } path)
        {
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }

    private void OnOpenElsewhere(object? sender, RoutedEventArgs e)
    {
        if (Viewer?.Location is { } path)
        {
            Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private static void Start(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Win32Exception)
        {
            // Gone just now, or blocked by policy: the window stays as it is.
        }
    }
}
