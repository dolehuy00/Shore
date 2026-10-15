using Android.Content;
using Android.Provider;
using AndroidUri = Android.Net.Uri;
using PowerManager = Android.OS.PowerManager;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// "Luôn nhận file" only works if the system leaves the app alone. Android's own exemption is asked for here;
/// phone makers' battery savers (ColorOS, MIUI…) kill apps regardless and need their own setting, which apps
/// cannot change (docs/14-android.md, A4).
/// </summary>
internal static class BatteryOptimization
{
    public static bool IsExempt(Context context) =>
        ((PowerManager)context.GetSystemService(Context.PowerService)!).IsIgnoringBatteryOptimizations(context.PackageName);

    /// <summary>Shows the system dialog "Cho phép ứng dụng luôn chạy trong nền?" unless already exempt.</summary>
    public static void RequestExemption(Context context)
    {
        if (IsExempt(context))
        {
            return;
        }

        Intent intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations)
            .SetData(AndroidUri.Parse($"package:{context.PackageName}"))!
            .AddFlags(ActivityFlags.NewTask)!;
        context.StartActivity(intent);
    }
}
