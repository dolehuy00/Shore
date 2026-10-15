using Android.Content;

namespace Shorekeeper.Android.Platform;

/// <summary>Starts the app again in a new process, e.g. so a restored identity is loaded before anything uses the old one.</summary>
internal static class AppRestart
{
    public static void Restart(Context context)
    {
        Intent launch = context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!;
        context.StartActivity(Intent.MakeRestartActivityTask(launch.Component));
        Java.Lang.Runtime.GetRuntime()!.Exit(0);
    }
}
