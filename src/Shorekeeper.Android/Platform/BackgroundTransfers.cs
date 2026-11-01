using Android.Content;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// Decides when <see cref="TransferService"/> runs (docs/14-android.md §3.3): while files move in either direction,
/// while someone may still download what we sent, or always with "Luôn nhận file". Also posts the notification
/// for offers that arrive.
/// </summary>
internal static class BackgroundTransfers
{
    private const string PreferencesName = "shorekeeper";
    private const string AlwaysReceiveKey = "always_receive";

    private static readonly Lock Gate = new();
    private static Context? context;
    private static InboxService? inbox;
    private static OfferService? offers;
    private static PeerNaming? naming;
    private static ILogger? logger;
    private static TransferService? service;

    /// <summary>"Luôn nhận file": stay reachable all the time, at the cost of a permanent notification and some battery.</summary>
    public static bool AlwaysReceive
    {
        get => Preferences()?.GetBoolean(AlwaysReceiveKey, false) ?? false;
        set
        {
            Preferences()?.Edit()!.PutBoolean(AlwaysReceiveKey, value)!.Apply();
            Recheck();
        }
    }

    public static void Start(Context appContext, IServiceProvider services)
    {
        context = appContext;
        inbox = services.GetRequiredService<InboxService>();
        offers = services.GetRequiredService<OfferService>();
        naming = services.GetRequiredService<PeerNaming>();
        logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(BackgroundTransfers));
        Notifications.CreateChannels(appContext);

        inbox.OfferArrived += (_, offer) =>
        {
            // Auto-accepted offers are already downloading; nothing to ask. Text arrives received, with [Sao chép].
            if (offer.State == InboxState.New || offer.Files.Count == 0)
            {
                Notifications.ShowOffer(appContext, offer, naming.Of(offer.SenderId));
            }

            Recheck();
        };
        inbox.Changed += (_, key) =>
        {
            // Answered offers lose their [Tải] [Bỏ qua]; text keeps its notification, as it arrives already received.
            if (inbox.Get(key) is { State: not InboxState.New, Files.Count: > 0 })
            {
                Notifications.CancelOffer(appContext, key);
            }

            Recheck();
        };
        offers.Changed += (_, _) => Recheck();
        Recheck();
    }

    /// <summary>The phone has started: with "Luôn nhận file" on, the app runs again and is reachable, as before.</summary>
    public static void Boot(Context appContext)
    {
        if (appContext.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.GetBoolean(AlwaysReceiveKey, false) == true)
        {
            appContext.StartForegroundService(new Intent(appContext, typeof(TransferService)).SetAction(TransferService.ActionKeep));
        }
    }

    public static void Attach(TransferService running)
    {
        lock (Gate)
        {
            service = running;
        }
    }

    /// <summary>Starts the service when there is a reason to, and lets it stop or update otherwise.</summary>
    public static void Recheck()
    {
        if (context is null)
        {
            return;
        }

        bool keep = IsBusy() || AlwaysReceive;
        TransferService? running;
        lock (Gate)
        {
            running = TransferService.IsRunning ? service : null;
        }

        if (running is not null)
        {
            running.Update(keep, Describe());
            return;
        }

        if (!keep)
        {
            return;
        }

        try
        {
            context.StartForegroundService(new Intent(context, typeof(TransferService)).SetAction(TransferService.ActionKeep));
        }
        catch (Java.Lang.IllegalStateException ex)
        {
            // Android 12+ refuses to start it from the background; it starts with the next tap in the app or on a notification.
            logger?.LogWarning("Foreground service not allowed now: {Message}", ex.Message);
        }
    }

    /// <summary>Text of the ongoing notification.</summary>
    public static string Describe()
    {
        int receiving = inbox?.GetOffers().Count(o => o.State == InboxState.Downloading) ?? 0;
        int sending = SendingOffers();
        return receiving > 0 && sending > 0 ? L.F("Đang nhận {0} và gửi {1} lượt file", receiving, sending)
            : receiving > 0 ? L.F("Đang nhận {0} lượt file", receiving)
            : sending > 0 ? L.F("Chờ người nhận tải {0} lượt file bạn gửi", sending)
            : L.T("Luôn nhận file: đang chờ");
    }

    private static bool IsBusy() =>
        (inbox?.GetOffers().Any(o => o.State == InboxState.Downloading) ?? false) || SendingOffers() > 0;

    /// <summary>Open offers someone may still download from us; they need the phone reachable.</summary>
    private static int SendingOffers() =>
        offers?.GetOffers().Count(o => o.State == OfferState.Open
            && o.Recipients.Any(r => r.State is RecipientState.Pending or RecipientState.Delivered or RecipientState.Downloading)) ?? 0;

    private static ISharedPreferences? Preferences() => context?.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
}
