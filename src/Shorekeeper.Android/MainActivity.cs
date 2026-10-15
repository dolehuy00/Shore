using Android.Content.PM;
using Avalonia.Android;

namespace Shorekeeper.Android;

/// <summary>
/// The only activity. singleTask: Avalonia has one main view, so anything that opens the app (e.g. the share
/// sheet later) must reach this activity rather than create a second one (docs/14-android.md, A0).
/// </summary>
[Activity(Label = "Shorekeeper", Theme = "@style/ShorekeeperTheme", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity;
