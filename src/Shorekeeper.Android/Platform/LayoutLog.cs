using System.Globalization;
using Android.Views;
using AndroidX.Core.View;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AndroidLog = Android.Util.Log;
using InsetsType = AndroidX.Core.View.WindowInsetsCompat.Type;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// Diagnostics for the layout that now and then ends up taller than the screen (the header squashed, the tabs under the
/// navigation bar), which has not been reproduced on demand: whenever the activity comes back or changes size, what
/// Android gives the app and what Avalonia laid out, right away and once things settled. Logcat keeps only minutes on a
/// busy phone, so the lines also go to a small file:
/// <c>adb pull /sdcard/Android/data/dev.shorekeeper.app/files/layout.log</c> (and <c>layout.log.old</c>).
/// </summary>
internal static class LayoutLog
{
    private const string Tag = "Shorekeeper";
    private const long MaxFileBytes = 256 * 1024;

    private static string? file;

    /// <summary>The app's one view; its header and tab strip show where the layout went wrong.</summary>
    public static Views.MainView? View { get; set; }

    /// <summary>Logs the Android side now and Avalonia's after its next layout and half a second later.</summary>
    public static void Event(string reason, Activity? activity = null)
    {
        if ((activity ?? MainActivity.Current) is { } shown)
        {
            Native(reason, shown);
        }

        Dispatcher.UIThread.Post(() => Avalonia(reason), DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() => Avalonia(reason + " +500ms"), TimeSpan.FromMilliseconds(500));
    }

    private static void Native(string reason, Activity activity)
    {
        if (activity.Window is not { } window)
        {
            return;
        }

        file ??= activity.GetExternalFilesDir(null) is { } directory ? Path.Combine(directory.AbsolutePath, "layout.log") : null;

        View decor = window.DecorView;
        View? content = activity.FindViewById(global::Android.Resource.Id.Content);
        int[] at = new int[2];
        content?.GetLocationOnScreen(at);
        WindowInsetsCompat? insets = ViewCompat.GetRootWindowInsets(decor);
        AndroidX.Core.Graphics.Insets? bars = insets?.GetInsets(InsetsType.SystemBars());
        global::Android.Content.Res.Configuration? config = activity.Resources?.Configuration;
        Write(
            $"{reason} | android: decor {decor.Width}x{decor.Height}, content {content?.Width}x{content?.Height} at {at[0]},{at[1]}, " +
            $"bars {bars?.Top}/{bars?.Bottom}, ime {insets?.IsVisible(InsetsType.Ime())}, " +
            $"config {config?.ScreenWidthDp}x{config?.ScreenHeightDp}dp {config?.Orientation}, multiWindow {activity.IsInMultiWindowMode}");
    }

    private static void Avalonia(string reason)
    {
        if (View is not { } view || TopLevel.GetTopLevel(view) is not { } top)
        {
            return;
        }

        Write(
            string.Create(CultureInfo.InvariantCulture,
                $"{reason} | avalonia: client {Size(top.ClientSize)} x{top.RenderScaling:0.###}, " +
                $"view {Size(view.Bounds.Size)}, header {Place(view.Header, view)}, tabs {Place(view.Tabs, view)}"));
    }

    private static string Place(Control control, Visual relativeTo) =>
        control.TranslatePoint(default, relativeTo) is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"{Size(control.Bounds.Size)} at {at.X:0.#},{at.Y:0.#}")
            : "not placed";

    private static string Size(Size size) => string.Create(CultureInfo.InvariantCulture, $"{size.Width:0.#}x{size.Height:0.#}");

    private static void Write(string message)
    {
        AndroidLog.Info(Tag, "Layout: " + message);
        if (file is null)
        {
            return;
        }

        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > MaxFileBytes)
            {
                File.Move(file, file + ".old", overwrite: true);
            }

            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n");
        }
        catch (IOException)
        {
            // Diagnostics only: a full disk must not break the app.
        }
    }
}
