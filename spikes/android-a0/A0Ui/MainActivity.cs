using Android.Content;
using Android.Content.PM;
using Android.Provider;
using Avalonia.Android;
using Build = Android.OS.Build;
using Stopwatch = System.Diagnostics.Stopwatch;
using Uri = Android.Net.Uri;

namespace A0;

/// <summary>
/// T6: receives files from the Android share sheet (content:// URIs), reads name, size and the whole stream.
/// T7: starts the TLS server with or without the foreground service. Actions also come as an intent extra
/// ("action" = server | fgs-start | fgs-stop) so the test can be driven with adb.
/// </summary>
[Activity(Name = "dev.shorekeeper.a0ui.MainActivity", Label = "Shorekeeper A0b", Theme = "@style/A0Theme", MainLauncher = true, Exported = true,
    // One activity only: Avalonia has a single main view, so a share must reach the running activity (OnNewIntent)
    // instead of creating a second one in the sending app's task.
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "*/*")]
public sealed class MainActivity : AvaloniaMainActivity
{
    private CancellationTokenSource? activityServer;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        App.ActionRequested += (_, action) => Run(action);
        SpikeLog.Write($"T5 Avalonia view up · Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})");
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1);
        }

        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void HandleIntent(Intent? intent)
    {
        if (intent?.GetStringExtra("action") is { } action)
        {
            Run(action);
        }

        if (intent?.Action is Intent.ActionSend or Intent.ActionSendMultiple)
        {
            List<Uri> uris = [];
            if (intent.ClipData is { } clip)
            {
                for (int i = 0; i < clip.ItemCount; i++)
                {
                    if (clip.GetItemAt(i)?.Uri is { } uri)
                    {
                        uris.Add(uri);
                    }
                }
            }

            // Not every sender fills ClipData (adb's "am start" does not); EXTRA_STREAM is the documented place.
            if (uris.Count == 0 && intent.Action == Intent.ActionSend && GetStream(intent) is { } single)
            {
                uris.Add(single);
            }
            else if (uris.Count == 0 && GetStreams(intent) is { } many)
            {
                uris.AddRange(many);
            }

            SpikeLog.Write($"T6 share received: {intent.Action}, {uris.Count} item(s), type {intent.Type}");
            _ = Task.Run(() => ReadAll(uris));
        }
    }

    private void Run(string action)
    {
        switch (action)
        {
            case "server" when activityServer is null:
                activityServer = new CancellationTokenSource();
                var server = new BlobServer(DeviceCert.Create(), TransferService.Port, line => SpikeLog.Write("activity " + line));
                _ = Task.Run(() => server.RunAsync(activityServer.Token));
                break;
            case "fgs-start":
                activityServer?.Cancel();
                activityServer = null;
                StartForegroundService(new Intent(this, typeof(TransferService)));
                break;
            case "fgs-stop":
                StopService(new Intent(this, typeof(TransferService)));
                break;
        }
    }

    private static Uri? GetStream(Intent intent) =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Uri))) as Uri
#pragma warning disable CA1422 // The typed overload exists from API 33 only.
            : intent.GetParcelableExtra(Intent.ExtraStream) as Uri;
#pragma warning restore CA1422

    private static IEnumerable<Uri>? GetStreams(Intent intent) =>
        (OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent.GetParcelableArrayListExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Uri)))
#pragma warning disable CA1422
            : intent.GetParcelableArrayListExtra(Intent.ExtraStream))?.OfType<Uri>();
#pragma warning restore CA1422

    /// <summary>What the real app needs from a shared item: display name, size, and a stream to send.</summary>
    private void ReadAll(List<Uri> uris)
    {
        foreach (Uri uri in uris)
        {
            try
            {
                string name = "?";
                long size = -1;
                using (var cursor = ContentResolver!.Query(uri, [IOpenableColumns.DisplayName, IOpenableColumns.Size], null, null, null))
                {
                    if (cursor is not null && cursor.MoveToFirst())
                    {
                        name = cursor.GetString(0) ?? "?";
                        size = cursor.IsNull(1) ? -1 : cursor.GetLong(1);
                    }
                }

                var watch = Stopwatch.StartNew();
                long read = 0;
                using (Stream stream = ContentResolver.OpenInputStream(uri)!)
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int n;
                    while ((n = stream.Read(buffer)) > 0)
                    {
                        read += n;
                    }
                }

                SpikeLog.Write($"T6 {uri.Scheme}://{uri.Authority} \"{name}\" size {size}, read {read} bytes " +
                    $"at {read / 1048576.0 / Math.Max(watch.Elapsed.TotalSeconds, 0.001):F0} MB/s");
            }
            catch (Exception ex) when (ex is IOException or Java.Lang.SecurityException or Java.IO.IOException)
            {
                SpikeLog.Write($"T6 FAIL {uri}: {ex.GetType().Name} {ex.Message}");
            }
        }
    }
}
