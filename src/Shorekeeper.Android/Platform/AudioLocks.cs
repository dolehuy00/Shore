using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using WifiMode = Android.Net.WifiMode;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// Held while sound streams in or out: Wi-Fi's low-latency mode (honoured while the app is in front with the screen
/// on), the high-performance mode for the rest of the time, and a partial wake lock.
/// </summary>
internal sealed class AudioLocks : IDisposable
{
    private readonly WifiManager.WifiLock[] wifiLocks;
    private readonly PowerManager.WakeLock wakeLock;

    public AudioLocks(Context context)
    {
        var wifi = (WifiManager)context.GetSystemService(Context.WifiService)!;
#pragma warning disable CA1422 // Deprecated on API 34, still what keeps the radio awake with the screen off.
        wifiLocks = [wifi.CreateWifiLock(WifiMode.FullLowLatency, "shorekeeper-audio")!, wifi.CreateWifiLock(WifiMode.FullHighPerf, "shorekeeper-audio")!];
#pragma warning restore CA1422
        wakeLock = ((PowerManager)context.GetSystemService(Context.PowerService)!).NewWakeLock(WakeLockFlags.Partial, "shorekeeper:audio")!;
        foreach (WifiManager.WifiLock wifiLock in wifiLocks)
        {
            wifiLock.Acquire();
        }

        wakeLock.Acquire();
    }

    public void Dispose()
    {
        foreach (WifiManager.WifiLock wifiLock in wifiLocks.Where(l => l.IsHeld))
        {
            wifiLock.Release();
        }

        wakeLock.Release();
    }
}
