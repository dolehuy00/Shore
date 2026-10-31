using Android.Content;
using Android.Graphics.Drawables;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Localization;
using Shorekeeper.UI.ViewModels;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Trust;
using AndroidResource = Android.Resource;
using AndroidUri = Android.Net.Uri;
using DeviceId = Shorekeeper.Core.Identity.DeviceId;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// A notification per conversation with unread messages (docs/15-chat.md §6.2), in Android's messaging style, with
/// "Trả lời" typed right on it and "Đánh dấu đã đọc". It goes away once the conversation is read. A group's says who
/// wrote each message under the group's name (docs/16-group-chat.md §6.2); none for a group whose notifications are off.
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

    /// <summary>Set by the app: the group conversation on screen.</summary>
    public static Func<string, bool>? IsReadingGroup { get; set; }

    public static void Start(Context context, IServiceProvider services)
    {
        var chat = services.GetRequiredService<ChatService>();
        var groupChat = services.GetRequiredService<GroupChatService>();
        var groups = services.GetRequiredService<GroupStore>();
        var naming = services.GetRequiredService<PeerNaming>();
        Manager(context).CreateNotificationChannel(new NotificationChannel(Channel, L.T("Tin nhắn"), NotificationImportance.High));

        chat.Received += (_, message) =>
        {
            if (IsReading?.Invoke(message.Peer) != true)
            {
                _ = ShowAsync(context, chat, naming.Of(message.Peer), message.Peer);
            }
        };
        // Read here, in the app or on another notification's button: nothing left to show.
        chat.Changed += (_, peer) => _ = CancelIfReadAsync(context, chat, peer);

        // Messages passed on from before we joined come read and raise nothing.
        groupChat.Received += (_, message) =>
        {
            if (IsReadingGroup?.Invoke(message.GroupId) != true)
            {
                _ = ShowGroupAsync(context, groupChat, groups, naming, message.GroupId);
            }
        };
        groupChat.Changed += (_, groupId) => _ = CancelGroupIfReadAsync(context, groupChat, groupId);
    }

    private static async Task ShowGroupAsync(Context context, GroupChatService groupChat, GroupStore groups, PeerNaming naming, string groupId)
    {
        IReadOnlyList<GroupChatMessage> messages = await groupChat.GetMessagesAsync(groupId, CancellationToken.None);
        List<GroupChatMessage> unread = [.. messages.Where(m => m.State == ChatMessageState.Unread).TakeLast(MaxShown)];
        // "Tắt thông báo" still lets through a message that names us (docs/18-chat-extras.md §4).
        if (unread.Count == 0
            || ((await groupChat.GetMutedAsync(CancellationToken.None)).Contains(groupId) && !unread.Any(groupChat.IsMentioned)))
        {
            return;
        }

        Group? group = groups.Get(groupId);
        string name = (await groupChat.GetConversationsAsync(CancellationToken.None)).FirstOrDefault(c => c.GroupId == groupId)?.Name ?? group?.Name ?? "";
        var style = new Notification.MessagingStyle(new Person.Builder().SetName(L.T("Bạn"))!.Build()!);
        style.SetConversationTitle(name);
        style.SetGroupConversation(true);
        foreach (GroupChatMessage message in unread)
        {
            string author = naming.Of(message.Author, group?.Members.FirstOrDefault(m => m.DeviceId == message.Author)?.Name ?? message.AuthorName);
            Person who = new Person.Builder().SetName(author)!.SetKey(message.Author.Value)!.Build()!;
            style.AddMessage(new Notification.MessagingStyle.Message(
                ChatBubbleViewModel.TextOf(message.Kind, message.Text, message.DurationMs), message.SentAt.ToUnixTimeMilliseconds(), who));
        }

        AndroidUri data = AndroidUri.Parse($"shorekeeper://chat-group/{groupId}")!;
        Intent action(string what) => new Intent(context, typeof(ChatActions)).SetAction(what)!.SetData(data)!.PutExtra(ChatActions.ExtraGroup, groupId)!;
        Intent open = new Intent(context, typeof(MainActivity)).PutExtra(MainActivity.ExtraChatGroup, groupId)!.SetData(data)!;
        Notify(context, GroupTag(groupId), style, name, action(ChatActions.ActionReply), action(ChatActions.ActionRead), open);
    }

    private static async Task CancelGroupIfReadAsync(Context context, GroupChatService groupChat, string groupId)
    {
        IReadOnlyList<GroupChatConversation> conversations = await groupChat.GetConversationsAsync(CancellationToken.None);
        if (conversations.FirstOrDefault(c => c.GroupId == groupId) is not { Unread: > 0 })
        {
            Manager(context).Cancel(GroupTag(groupId), NotificationId);
        }
    }

    private static string GroupTag(string groupId) => "group:" + groupId;

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

        Intent open = new Intent(context, typeof(MainActivity))
            .PutExtra(MainActivity.ExtraChat, peer.Value)!
            .SetData(AndroidUri.Parse($"shorekeeper://chat/{peer.Value}"))!;
        Notify(context, peer.Value, style, name, Intent(context, ChatActions.ActionReply, peer), Intent(context, ChatActions.ActionRead, peer), open);
    }

    /// <param name="tag">Which conversation: one notification each.</param>
    /// <param name="replyTo">Who "Trả lời" answers: the person, or the group.</param>
    private static void Notify(Context context, string tag, Notification.MessagingStyle style, string replyTo, Intent reply, Intent read, Intent open)
    {
        // A reply needs a mutable intent: Android adds the typed text to it (mutable is the default before Android 12).
        PendingIntentFlags mutable = OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0;
        int code = tag.GetHashCode();
        Notification notification = new Notification.Builder(context, Channel)
            .SetStyle(style)!
            .SetSmallIcon(Resource.Drawable.ic_stat_shorekeeper)!
            .SetCategory(Notification.CategoryMessage)!
            .SetAutoCancel(true)!
            .SetContentIntent(PendingIntent.GetActivity(context, code, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent))!
            .AddAction(new Notification.Action.Builder(
                    Icon.CreateWithResource(context, AndroidResource.Drawable.IcMenuSend), L.T("Trả lời"),
                    PendingIntent.GetBroadcast(context, code, reply, mutable | PendingIntentFlags.UpdateCurrent))
                .AddRemoteInput(new RemoteInput.Builder(ChatActions.ReplyKey).SetLabel(L.F("Trả lời {0}", replyTo))!.Build())!
                .SetAllowGeneratedReplies(true)!
                .Build())!
            .AddAction(new Notification.Action.Builder(
                    Icon.CreateWithResource(context, AndroidResource.Drawable.IcMenuView), L.T("Đánh dấu đã đọc"),
                    PendingIntent.GetBroadcast(context, code, read, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent))
                .Build())!
            .Build()!;
        Manager(context).Notify(tag, NotificationId, notification);
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
    public const string ExtraGroup = "group";
    public const string ReplyKey = "reply";

    public override void OnReceive(Context? context, Intent? intent)
    {
        string? groupId = intent?.GetStringExtra(ExtraGroup);
        DeviceId peer = default;
        if (context is null || intent is null || (groupId is null && (intent.GetStringExtra(ExtraPeer) is not { } id || !DeviceId.TryParse(id, out peer))))
        {
            return;
        }

        string? text = intent.Action == ActionReply ? RemoteInput.GetResultsFromIntent(intent)?.GetCharSequence(ReplyKey)?.ToString() : null;
        bool send = !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= ChatService.MaxTextLength;
        Context app = context.ApplicationContext ?? context;
        PendingResult? pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                IServiceProvider services = await EngineHost.StartAsync(app);
                if (groupId is not null)
                {
                    var groupChat = services.GetRequiredService<GroupChatService>();
                    // No longer in the group (or alone in it): nothing to send, but still read.
                    if (send && groupChat.OthersIn(groupId) is { Count: > 0 })
                    {
                        await groupChat.SendTextAsync(groupId, text!, CancellationToken.None);
                    }

                    await groupChat.MarkReadAsync(groupId, CancellationToken.None);
                    return;
                }

                var chat = services.GetRequiredService<ChatService>();
                if (send)
                {
                    await chat.SendTextAsync(peer, text!, CancellationToken.None);
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
