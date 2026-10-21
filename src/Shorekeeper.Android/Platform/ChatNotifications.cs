using Android.Content;
using Android.Graphics.Drawables;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Trust;
using AndroidResource = Android.Resource;
using AndroidUri = Android.Net.Uri;
using DeviceId = Shorekeeper.Core.Identity.DeviceId;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// A notification per conversation with unread messages (docs/15-chat.md §6.2), in Android's messaging style, with
/// "Trả lời" typed right on it and "Đánh dấu đã đọc". It goes away once the conversation is read.
/// </summary>
internal static class ChatNotifications
{
    private const string Channel = "messages";
    // Tagged with the peer, so it never meets the "Nghe" notification's 3; a number of its own all the same.
    private const int NotificationId = 5;

    /// <summary>How many of the latest unread messages the notification lists.</summary>
    private const int MaxShown = 8;

    /// <summary>Set by the app: the conversation on screen, which needs no notification.</summary>
    public static Func<DeviceId, bool>? IsReading { get; set; }

    public static void Start(Context context, IServiceProvider services)
    {
        var chat = services.GetRequiredService<ChatService>();
        var trust = services.GetRequiredService<TrustStore>();
        Manager(context).CreateNotificationChannel(new NotificationChannel(Channel, L.T("Tin nhắn"), NotificationImportance.High));

        chat.Received += (_, message) =>
        {
            if (IsReading?.Invoke(message.Peer) != true)
            {
                _ = ShowAsync(context, chat, trust.Get(message.Peer)?.ShownName ?? message.Peer.ShortForm, message.Peer);
            }
        };
        // Read here, in the app or on another notification's button: nothing left to show.
        chat.Changed += (_, peer) => _ = CancelIfReadAsync(context, chat, peer);
    }

    private static async Task ShowAsync(Context context, ChatService chat, string name, DeviceId peer)
    {
        IReadOnlyList<ChatMessage> messages = await chat.GetMessagesAsync(peer, CancellationToken.None);
        List<ChatMessage> unread = [.. messages.Where(m => m.State == ChatMessageState.Unread).TakeLast(MaxShown)];
        if (unread.Count == 0)
        {
            return;
        }

        Person them = new Person.Builder().SetName(name)!.SetKey(peer.Value)!.Build()!;
        var style = new Notification.MessagingStyle(new Person.Builder().SetName(L.T("Bạn"))!.Build()!);
        foreach (ChatMessage message in unread)
        {
            style.AddMessage(new Notification.MessagingStyle.Message(ChatBubbleViewModel.TextOf(message), message.SentAt.ToUnixTimeMilliseconds(), them));
        }

        // A reply needs a mutable intent: Android adds the typed text to it (mutable is the default before Android 12).
        PendingIntentFlags mutable = OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0;
        PendingIntent reply = PendingIntent.GetBroadcast(
            context, peer.Value.GetHashCode(), Intent(context, ChatActions.ActionReply, peer), mutable | PendingIntentFlags.UpdateCurrent)!;
        PendingIntent read = PendingIntent.GetBroadcast(
            context, peer.Value.GetHashCode(), Intent(context, ChatActions.ActionRead, peer), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        Intent open = new Intent(context, typeof(MainActivity))
            .PutExtra(MainActivity.ExtraChat, peer.Value)!
            .SetData(AndroidUri.Parse($"shorekeeper://chat/{peer.Value}"))!;

        Notification notification = new Notification.Builder(context, Channel)
            .SetStyle(style)!
            .SetSmallIcon(Resource.Drawable.ic_stat_shorekeeper)!
            .SetCategory(Notification.CategoryMessage)!
            .SetAutoCancel(true)!
            .SetContentIntent(PendingIntent.GetActivity(context, peer.Value.GetHashCode(), open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent))!
            .AddAction(new Notification.Action.Builder(Icon.CreateWithResource(context, AndroidResource.Drawable.IcMenuSend), L.T("Trả lời"), reply)
                .AddRemoteInput(new RemoteInput.Builder(ChatActions.ReplyKey).SetLabel(L.F("Trả lời {0}", name))!.Build())!
                .SetAllowGeneratedReplies(true)!
                .Build())!
            .AddAction(new Notification.Action.Builder(Icon.CreateWithResource(context, AndroidResource.Drawable.IcMenuView), L.T("Đánh dấu đã đọc"), read).Build())!
            .Build()!;
        Manager(context).Notify(peer.Value, NotificationId, notification);
    }

    private static async Task CancelIfReadAsync(Context context, ChatService chat, DeviceId peer)
    {
        IReadOnlyList<ChatConversation> conversations = await chat.GetConversationsAsync(CancellationToken.None);
        if (conversations.FirstOrDefault(c => c.Peer == peer) is not { Unread: > 0 })
        {
            Manager(context).Cancel(peer.Value, NotificationId);
        }
    }

    // The data URI makes each conversation's intent distinct, so the system does not reuse another one's extras.
    private static Intent Intent(Context context, string action, DeviceId peer) =>
        new Intent(context, typeof(ChatActions))
            .SetAction(action)!
            .SetData(AndroidUri.Parse($"shorekeeper://chat/{peer.Value}"))!
            .PutExtra(ChatActions.ExtraPeer, peer.Value)!;

    private static NotificationManager Manager(Context context) => (NotificationManager)context.GetSystemService(Context.NotificationService)!;
}

/// <summary>"Trả lời" and "Đánh dấu đã đọc" on a message notification, without opening the app. Not exported.</summary>
[BroadcastReceiver(Name = "dev.shorekeeper.app.ChatActions", Exported = false)]
public sealed class ChatActions : BroadcastReceiver
{
    public const string ActionReply = "dev.shorekeeper.app.chat.REPLY";
    public const string ActionRead = "dev.shorekeeper.app.chat.READ";
    public const string ExtraPeer = "peer";
    public const string ReplyKey = "reply";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.GetStringExtra(ExtraPeer) is not { } id || !DeviceId.TryParse(id, out DeviceId peer))
        {
            return;
        }

        string? text = intent.Action == ActionReply ? RemoteInput.GetResultsFromIntent(intent)?.GetCharSequence(ReplyKey)?.ToString() : null;
        Context app = context.ApplicationContext ?? context;
        PendingResult? pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                var chat = (await EngineHost.StartAsync(app)).GetRequiredService<ChatService>();
                if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length <= ChatService.MaxTextLength)
                {
                    await chat.SendTextAsync(peer, text, CancellationToken.None);
                }

                // Answering is reading: the notification goes with the unread messages.
                await chat.MarkReadAsync(peer, CancellationToken.None);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}
