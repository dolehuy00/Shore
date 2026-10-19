using Android.Content;
using Android.Content.PM;
using Android.Views;
using Avalonia.Android;
using AndroidUri = Android.Net.Uri;

namespace Shorekeeper.Android;

/// <summary>
/// The only activity, also the target of the share sheet ("Chia sẻ → Shorekeeper"). singleTask: Avalonia has one
/// main view, so a share must reach this activity rather than create a second one (docs/14-android.md, A0).
/// </summary>
[Activity(Label = "Shorekeeper", Theme = "@style/ShorekeeperTheme", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "*/*")]
public sealed class MainActivity : AvaloniaMainActivity
{
    /// <summary>Tab to show when a notification opens the app.</summary>
    public const string ExtraTab = "tab";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        PreferFastestRefreshRate();
        // Android 13+: without it, offers arrive silently. Asking again once granted still opens the system's
        // permission screen over the app for a moment, which slowed every start.
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 1);
        }

        if (savedInstanceState is null)
        {
            HandleShare(Intent);
            HandleTab(Intent);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleShare(intent);
        HandleTab(intent);
    }

    /// <summary>
    /// Asks for the display's fastest mode (120 Hz on many phones): Avalonia draws on its own surface, which the system
    /// otherwise runs at 60 Hz.
    /// </summary>
    private void PreferFastestRefreshRate()
    {
        if (Window is not { } window || (OperatingSystem.IsAndroidVersionAtLeast(30) ? Display : WindowManager?.DefaultDisplay) is not { } display)
        {
            return;
        }

        if (display.GetMode() is not { } current)
        {
            return;
        }

        Display.Mode? fastest = display.GetSupportedModes()?
            .OfType<Display.Mode>()
            .Where(m => m.PhysicalWidth == current.PhysicalWidth && m.PhysicalHeight == current.PhysicalHeight)
            .MaxBy(m => m.RefreshRate);
        if (fastest is not null && fastest.ModeId != current.ModeId)
        {
            WindowManagerLayoutParams attributes = window.Attributes!;
            attributes.PreferredDisplayModeId = fastest.ModeId;
            window.Attributes = attributes;
        }
    }

    private static void HandleTab(Intent? intent)
    {
        if (intent is not null && !intent.Flags.HasFlag(ActivityFlags.LaunchedFromHistory) && intent.GetIntExtra(ExtraTab, -1) is >= 0 and var tab)
        {
            App.ShowTab(tab);
        }
    }

    private static void HandleShare(Intent? intent)
    {
        // Opening the app from Recents delivers the last share again, and its read permission is gone by then.
        if (intent is null || intent.Flags.HasFlag(ActivityFlags.LaunchedFromHistory)
            || intent.Action is not (Intent.ActionSend or Intent.ActionSendMultiple))
        {
            return;
        }

        List<string> locations = [];
        if (intent.ClipData is { } clip)
        {
            for (int i = 0; i < clip.ItemCount; i++)
            {
                if (clip.GetItemAt(i)?.Uri is { } uri)
                {
                    locations.Add(uri.ToString()!);
                }
            }
        }

        // Not every app fills ClipData; EXTRA_STREAM is where the share sheet documents them.
        if (locations.Count == 0)
        {
            locations.AddRange(Streams(intent).Select(uri => uri.ToString()!));
        }

        // A link or text shared from a browser, a chat app…: EXTRA_TEXT, alone or with files.
        string? text = intent.GetStringExtra(Intent.ExtraText);
        if (locations.Count > 0 || !string.IsNullOrWhiteSpace(text))
        {
            App.Share(locations, string.IsNullOrWhiteSpace(text) ? null : text);
        }
    }

    private static IEnumerable<AndroidUri> Streams(Intent intent)
    {
        if (intent.Action == Intent.ActionSend)
        {
            AndroidUri? single = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(AndroidUri))) as AndroidUri
#pragma warning disable CA1422 // The typed overload only exists from API 33.
                : intent.GetParcelableExtra(Intent.ExtraStream) as AndroidUri;
#pragma warning restore CA1422
            return single is null ? [] : [single];
        }

        var many = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent.GetParcelableArrayListExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(AndroidUri)))
#pragma warning disable CA1422
            : intent.GetParcelableArrayListExtra(Intent.ExtraStream);
#pragma warning restore CA1422
        return many?.OfType<AndroidUri>() ?? [];
    }
}
