using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.Runtime;
using IBinder = Android.OS.IBinder;
using PowerManager = Android.OS.PowerManager;
using WakeLockFlags = Android.OS.WakeLockFlags;
using WifiMode = Android.Net.WifiMode;

namespace A0;

/// <summary>
/// T7: foreground service (type connectedDevice) that keeps the TLS server answering with the screen off,
/// holding a high-performance Wi-Fi lock and a partial wake lock like a real transfer would.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class TransferService : Service
{
    public const int Port = 47510;
    private const string Channel = "transfers";

    private CancellationTokenSource? stop;
    private WifiManager.WifiLock? wifiLock;
    private PowerManager.WakeLock? wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        var notifications = (NotificationManager)GetSystemService(NotificationService)!;
        notifications.CreateNotificationChannel(new NotificationChannel(Channel, "Đang truyền file", NotificationImportance.Low));
        Notification notification = new Notification.Builder(this, Channel)
            .SetContentTitle("Shorekeeper A0b")!
            .SetContentText("Đang chờ nhận file (thử dịch vụ nền)")!
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)!
            .SetOngoing(true)!
            .Build()!;
        StartForeground(1, notification, ForegroundService.TypeConnectedDevice);

        if (stop is null)
        {
            var wifi = (WifiManager)GetSystemService(WifiService)!;
#pragma warning disable CA1422 // Deprecated on API 34; the low-latency mode needs the screen on, which is what this test avoids.
            wifiLock = wifi.CreateWifiLock(WifiMode.FullHighPerf, "shorekeeper-a0b");
#pragma warning restore CA1422
            wakeLock = ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "shorekeeper:a0b");
            wifiLock!.Acquire();
            wakeLock!.Acquire();

            stop = new CancellationTokenSource();
            var server = new BlobServer(DeviceCert.Create(), Port, line => SpikeLog.Write("fgs " + line));
            _ = Task.Run(() => server.RunAsync(stop.Token));
            SpikeLog.Write("fgs: started, locks held");
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        stop?.Cancel();
        stop?.Dispose();
        wifiLock?.Release();
        wakeLock?.Release();
        SpikeLog.Write("fgs: stopped, locks released");
        base.OnDestroy();
    }
}
