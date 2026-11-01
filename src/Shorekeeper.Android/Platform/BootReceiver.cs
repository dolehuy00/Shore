using Android.Content;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// After the phone restarts, "Luôn nhận file" carries on without opening the app (docs/14-android.md §3.3). Android
/// lets a connectedDevice foreground service start from here; some makers also want the app allowed to start itself.
/// </summary>
[BroadcastReceiver(Name = "dev.shorekeeper.app.BootReceiver", Exported = true)]
[IntentFilter([Intent.ActionBootCompleted])]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action == Intent.ActionBootCompleted && context?.ApplicationContext is { } app)
        {
            BackgroundTransfers.Boot(app);
        }
    }
}
