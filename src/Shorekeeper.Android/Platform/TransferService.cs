using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Engine.Transfers;
using IBinder = Android.OS.IBinder;
using PeerDeviceId = Shorekeeper.Core.Identity.DeviceId;
using PowerManager = Android.OS.PowerManager;
using WakeLockFlags = Android.OS.WakeLockFlags;
using WifiMode = Android.Net.WifiMode;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// Keeps the app reachable with the screen off while files move, or all the time with "Luôn nhận file"
/// (docs/14-android.md §3.3). A0 showed Doze cuts the network otherwise; this service holds a high-performance
/// Wi-Fi lock and a partial wake lock. It also carries the "Tải" / "Bỏ qua" buttons of offer notifications,
/// which work even when Android had closed the app.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class TransferService : Service
{
    public const string ActionKeep = "dev.shorekeeper.KEEP";
    public const string ActionDownload = "dev.shorekeeper.DOWNLOAD";
    public const string ActionDecline = "dev.shorekeeper.DECLINE";
    public const string ActionCopy = "dev.shorekeeper.COPY";
    public const string ExtraSender = "sender";
    public const string ExtraOffer = "offer";

    private const int NotificationId = 1;

    private WifiManager.WifiLock? wifiLock;
    private PowerManager.WakeLock? wakeLock;

    /// <summary>The service is in the foreground right now.</summary>
    public static bool IsRunning { get; private set; }

    public override IBinder? OnBind(Intent? intent) => null;

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        // Must happen within seconds of being started, whatever the request turns out to be.
        StartForeground(NotificationId, Notifications.Transfer(this, BackgroundTransfers.Describe()), ForegroundService.TypeConnectedDevice);
        if (!IsRunning)
        {
            IsRunning = true;
            AcquireLocks();
        }

        _ = HandleAsync(intent);
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        wifiLock?.Release();
        wakeLock?.Release();
        base.OnDestroy();
    }

    /// <summary>Refreshes the ongoing notification, or stops when there is nothing left to keep alive.</summary>
    internal void Update(bool keepRunning, string text)
    {
        if (keepRunning)
        {
            ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, Notifications.Transfer(this, text));
        }
        else
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    private async Task HandleAsync(Intent? intent)
    {
        IServiceProvider services = await EngineHost.StartAsync(ApplicationContext!);
        BackgroundTransfers.Attach(this);
        if (intent?.GetStringExtra(ExtraSender) is { } sender && PeerDeviceId.TryParse(sender, out PeerDeviceId senderId)
            && intent.GetStringExtra(ExtraOffer) is { } offerId)
        {
            var key = new OfferKey(senderId, offerId);
            InboxService inbox = services.GetRequiredService<InboxService>();
            Notifications.CancelOffer(this, key);
            if (intent.Action == ActionDownload)
            {
                inbox.Download(key);
            }
            else if (intent.Action == ActionDecline)
            {
                await inbox.DeclineAsync(key);
            }
            else if (intent.Action == ActionCopy && inbox.Get(key)?.Note is { } text)
            {
                TextActions.Copy(this, text);
            }
        }

        BackgroundTransfers.Recheck();
    }

    private void AcquireLocks()
    {
        var wifi = (WifiManager)GetSystemService(WifiService)!;
#pragma warning disable CA1422 // Deprecated on API 34; the low-latency mode only works with the screen on, which is what this avoids.
        wifiLock = wifi.CreateWifiLock(WifiMode.FullHighPerf, "shorekeeper-transfer");
#pragma warning restore CA1422
        wakeLock = ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "shorekeeper:transfer");
        wifiLock!.Acquire();
        wakeLock!.Acquire();
    }
}
