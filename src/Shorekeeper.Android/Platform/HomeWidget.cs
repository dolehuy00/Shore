using Android.Appwidget;
using Android.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Audio;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// The home screen widget (docs/14-android.md §3.8): a status line ("2 máy online", "Đang nghe PC · MSI"…) and
/// [Gửi file] [Gửi chữ đã chép] [Nghe máy tính]. The first two open the app; listening starts and stops without it.
/// The engine pushes every change while the process runs; adding the widget or restarting the phone starts the
/// process, and with it the engine.
/// </summary>
[BroadcastReceiver(Name = "dev.shorekeeper.app.HomeWidget", Label = "Shorekeeper", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_info")]
public sealed class HomeWidget : AppWidgetProvider
{
    private static readonly Lock Gate = new();
    private static IServiceProvider? engine;
    // The presence dots of the app (MainView.axaml): green online, gray offline.
    private const int Online = unchecked((int)0xFF22C55E);
    private const int Offline = unchecked((int)0xFF9CA3AF);
    private static (string Status, bool AnyOnline, bool Listening)? shown;

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null)
        {
            return;
        }

        // A new widget starts blank: show the buttons now and the status once the engine is up.
        lock (Gate)
        {
            shown = null;
        }

        Update(context);
        PendingResult? pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                await EngineHost.StartAsync(context.ApplicationContext ?? context);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }

    /// <summary>Called once the engine is up: keeps the widget in step with peers, contacts and listening.</summary>
    internal static void Watch(Context context, IServiceProvider services)
    {
        engine = services;
        services.GetRequiredService<PeerDirectory>().Changed += (_, _) => Update(context);
        services.GetRequiredService<TrustStore>().Changed += (_, _) => Update(context);
        services.GetRequiredService<AudioService>().Changed += (_, _) => Update(context);
        Update(context);
    }

    /// <summary>The computer "Nghe máy tính" listens to: a contact playing sound, computers before phones.</summary>
    internal static PeerInfo? Source(IServiceProvider services)
    {
        var trust = services.GetRequiredService<TrustStore>();
        return services.GetRequiredService<PeerDirectory>().Snapshot()
            .Where(p => p.State == PeerState.Online && p.IsPlayingAudio && trust.GetLevel(p.DeviceId) == TrustLevel.Trusted)
            .OrderBy(p => p.Os.Equals("android", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => NameOf(services, p.DeviceId, p), StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
    }

    internal static string NameOf(IServiceProvider services, DeviceId id, PeerInfo? live = null) =>
        services.GetRequiredService<TrustStore>().Get(id)?.ShownName ?? live?.DisplayName ?? id.ShortForm;

    private static void Update(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context)!;
        int[] ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(HomeWidget)))) ?? [];
        if (ids.Length == 0)
        {
            return;
        }

        IServiceProvider? services = engine;
        bool listening = services?.GetRequiredService<AudioService>().ListeningTo is not null;
        (string status, bool anyOnline) = services is null ? ("Shorekeeper", false) : StatusOf(services);
        lock (Gate)
        {
            if (shown == (status, anyOnline, listening))
            {
                return;
            }

            shown = (status, anyOnline, listening);
        }

        RemoteViews tall = Views(context, Resource.Layout.widget, status, anyOnline, listening, compact: false);
        // Android 12+ picks by size: one cell high, the icons sit beside shorter labels.
        RemoteViews views = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? new RemoteViews(new Dictionary<SizeF, RemoteViews>
            {
                [new SizeF(180, 50)] = Views(context, Resource.Layout.widget_compact, status, anyOnline, listening, compact: true),
                [new SizeF(180, 110)] = tall,
            })
            : tall;
        manager.UpdateAppWidget(ids, views);
    }

    private static RemoteViews Views(Context context, int layout, string status, bool anyOnline, bool listening, bool compact)
    {
        var views = new RemoteViews(context.PackageName, layout);
        views.SetTextViewText(Resource.Id.widget_status, status);
        views.SetInt(Resource.Id.widget_dot, "setColorFilter", anyOnline ? Online : Offline);
        views.SetTextViewText(Resource.Id.widget_files_label, L.T("Gửi file"));
        views.SetTextViewText(Resource.Id.widget_text_label, compact ? L.T("Gửi chữ") : L.T("Gửi chữ đã chép"));
        views.SetTextViewText(Resource.Id.widget_listen_label, compact ? L.T("Nghe PC") : L.T("Nghe máy tính"));
        views.SetTextViewText(Resource.Id.widget_listening_label, L.T("Dừng nghe"));
        views.SetViewVisibility(Resource.Id.widget_listen, listening ? ViewStates.Gone : ViewStates.Visible);
        views.SetViewVisibility(Resource.Id.widget_listening, listening ? ViewStates.Visible : ViewStates.Gone);
        views.SetOnClickPendingIntent(Resource.Id.widget_header, Open(context, null, requestCode: 20));
        views.SetOnClickPendingIntent(Resource.Id.widget_files, Open(context, MainActivity.WidgetFiles, requestCode: 21));
        views.SetOnClickPendingIntent(Resource.Id.widget_text, Open(context, MainActivity.WidgetText, requestCode: 22));
        PendingIntent listen = PendingIntent.GetBroadcast(
            context, 23, new Intent(context, typeof(WidgetActions)).SetAction(WidgetActions.ActionListen), PendingIntentFlags.Immutable)!;
        views.SetOnClickPendingIntent(Resource.Id.widget_listen, listen);
        views.SetOnClickPendingIntent(Resource.Id.widget_listening, listen);
        return views;
    }

    /// <returns>"Đang nghe PC · MSI", "PC · MSI đang phát âm thanh" or "2 máy online"; whether any contact is online.</returns>
    private static (string Text, bool AnyOnline) StatusOf(IServiceProvider services)
    {
        var trust = services.GetRequiredService<TrustStore>();
        int online = services.GetRequiredService<PeerDirectory>().Snapshot()
            .Count(p => p.State == PeerState.Online && trust.GetLevel(p.DeviceId) == TrustLevel.Trusted);
        string text = services.GetRequiredService<AudioService>().ListeningTo is { } to ? L.F("Đang nghe {0}", NameOf(services, to))
            : Source(services) is { } source ? L.F("{0} đang phát âm thanh", NameOf(services, source.DeviceId, source))
            : online == 0 ? L.T("Không có máy nào online")
            : L.F("{0} máy online", online);
        return (text, online > 0);
    }

    private static PendingIntent Open(Context context, string? action, int requestCode)
    {
        Intent intent = new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask);
        if (action is not null)
        {
            intent.PutExtra(MainActivity.ExtraWidget, action);
        }

        return PendingIntent.GetActivity(context, requestCode, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }
}

/// <summary>"Nghe máy tính" on the widget: listens or stops without opening the app. Not exported: only the widget sends it.</summary>
[BroadcastReceiver(Name = "dev.shorekeeper.app.WidgetActions", Exported = false)]
public sealed class WidgetActions : BroadcastReceiver
{
    public const string ActionListen = "dev.shorekeeper.app.widget.LISTEN";

    /// <summary>How long a process the tap just started waits to hear a computer that plays.</summary>
    private static readonly TimeSpan WaitForSource = TimeSpan.FromSeconds(4);

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != ActionListen)
        {
            return;
        }

        Context app = context.ApplicationContext ?? context;
        PendingResult? pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                if (await ToggleAsync(await EngineHost.StartAsync(app)) is { } message)
                {
                    new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!).Post(() => Toast.MakeText(app, message, ToastLength.Long)!.Show());
                }
            }
            finally
            {
                pending?.Finish();
            }
        });
    }

    /// <returns>What to tell the user, if anything.</returns>
    private static async Task<string?> ToggleAsync(IServiceProvider services)
    {
        var audio = services.GetRequiredService<AudioService>();
        if (audio.ListeningTo is not null)
        {
            await audio.StopListeningAsync();
            return null;
        }

        DateTime deadline = DateTime.UtcNow + WaitForSource;
        PeerInfo? source;
        while ((source = HomeWidget.Source(services)) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        if (source is null)
        {
            return L.T("Chưa có máy nào đang Phát âm thanh.");
        }

        string name = HomeWidget.NameOf(services, source.DeviceId, source);
        try
        {
            await audio.ListenAsync(source.DeviceId);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        catch (PeerApiException ex) when (ex.Code is ApiErrorCodes.PasswordRequired or ApiErrorCodes.WrongPassword)
        {
            return L.F("{0} cần mật khẩu: nghe một lần trong tab Âm thanh, sau đó widget tự dùng mật khẩu đó.", name);
        }
        catch (PeerApiException)
        {
            return L.F("{0} từ chối cho nghe.", name);
        }
        catch (Exception ex) when (ex is PeerUnreachableException or HttpRequestException)
        {
            return L.F("Không liên lạc được với {0}.", name);
        }
    }
}
