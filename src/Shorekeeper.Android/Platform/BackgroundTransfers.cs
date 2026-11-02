using Android.Content;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// Decides when <see cref="TransferService"/> runs (docs/14-android.md §3.3): while files move in either direction,
/// while someone may still download what we sent, or always with "Chạy nền". Also posts the notifications for offers
/// that arrive and for people asking to join a group we host (docs/20-phone-host.md).
/// </summary>
internal static class BackgroundTransfers
{
    private const string PreferencesName = "shorekeeper";
    private const string AlwaysReceiveKey = "always_receive";

    private static readonly Lock Gate = new();
    private static Context? context;
    private static InboxService? inbox;
    private static OfferService? offers;
    private static GroupStore? groups;
    private static PeerNaming? naming;

    /// <summary>Join requests whose notification shows; it goes once the request is answered, here or anywhere else.</summary>
    private static readonly HashSet<(string GroupId, DeviceId Peer)> AskedToJoin = [];
    private static ILogger? logger;
    private static TransferService? service;

    /// <summary>"Chạy nền": stay reachable all the time, at the cost of a permanent notification and some battery.</summary>
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
        GroupStore store = services.GetRequiredService<GroupStore>();
        groups = store;
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
        services.GetRequiredService<GroupService>().JoinRequested += (_, request) =>
        {
            lock (Gate)
            {
                AskedToJoin.Add((request.GroupId, request.PeerId));
            }

            Notifications.ShowJoinRequest(appContext, request, store.Get(request.GroupId)?.Name ?? "");
        };
        store.Changed += (_, _) =>
        {
            CancelAnswered(appContext, store);
            Recheck();
        };
        Recheck();
    }

    /// <summary>Approved or declined here, in the app or by the notification: its notification goes.</summary>
    private static void CancelAnswered(Context appContext, GroupStore store)
    {
        List<(string GroupId, DeviceId Peer)> answered;
        lock (Gate)
        {
            answered = [.. AskedToJoin.Where(r => !store.GetRequests(r.GroupId).Any(x => x.PeerId == r.Peer && x.State == JoinRequestState.Pending))];
            AskedToJoin.ExceptWith(answered);
        }

        foreach ((string groupId, DeviceId peer) in answered)
        {
            Notifications.CancelJoinRequest(appContext, groupId, peer);
        }
    }

    /// <summary>The phone has started: with "Chạy nền" on, the app runs again and is reachable, as before.</summary>
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
            running.Update(keep, Describe(), Progress());
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

    /// <summary>Of everything moving in either direction, 0 to 99; null when nothing moves (docs/19-transfers-preview.md §6 Q6).</summary>
    public static int? Progress()
    {
        long done = 0;
        long total = 0;
        foreach (ReceivedOffer offer in inbox?.GetOffers().Where(o => o.State == InboxState.Downloading) ?? [])
        {
            done += offer.BytesReceived;
            total += offer.TotalBytes;
        }

        foreach (SentOffer offer in offers?.GetOffers().Where(o => o.State == OfferState.Open) ?? [])
        {
            foreach (SentRecipient recipient in offer.Recipients.Where(r => r.State == RecipientState.Downloading))
            {
                done += recipient.BytesServed;
                total += offer.TotalBytes;
            }
        }

        return total == 0 ? null : (int)Math.Min(99, 100 * done / total);
    }

    /// <summary>Text of the ongoing notification; "· Host 2 nhóm" while we host groups (docs/20-phone-host.md, H1).</summary>
    public static string Describe()
    {
        int receiving = inbox?.GetOffers().Count(o => o.State == InboxState.Downloading) ?? 0;
        int sending = SendingOffers();
        string text = receiving > 0 && sending > 0 ? L.F("Đang nhận {0} và gửi {1} lượt file", receiving, sending)
            : receiving > 0 ? L.F("Đang nhận {0} lượt file", receiving)
            : sending > 0 ? L.F("Chờ người nhận tải {0} lượt file bạn gửi", sending)
            : L.T("Chạy nền: đang chờ");
        int hosting = groups?.GetAll().Count(g => g.Role == GroupRole.Host) ?? 0;
        return hosting > 0 ? L.F("{0} · Host {1} nhóm", text, hosting) : text;
    }

    private static bool IsBusy() =>
        (inbox?.GetOffers().Any(o => o.State == InboxState.Downloading) ?? false) || SendingOffers() > 0;

    /// <summary>Open offers someone may still download from us; they need the phone reachable.</summary>
    private static int SendingOffers() =>
        offers?.GetOffers().Count(o => o.State == OfferState.Open
            && o.Recipients.Any(r => r.State is RecipientState.Pending or RecipientState.Delivered or RecipientState.Downloading)) ?? 0;

    private static ISharedPreferences? Preferences() => context?.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
}
