using Shorekeeper.Engine.Chat;

namespace Shorekeeper.Engine.Tests;

/// <summary>Conversations read a page at a time (docs/22-scale.md S1), for a person and for a group.</summary>
public sealed class ChatPagingTests
{
    private static readonly ScaleSize Small = new()
    {
        Contacts = 2, BigConversation = 120, OtherConversations = 0, Reacted = 0.5, BigGroupMembers = 4, BigGroupMessages = 120, OursInGroup = 0.3,
        SmallGroups = 0, ReceivedOffers = 0, SentOffers = 0,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_conversation_is_read_a_page_at_a_time_in_order()
    {
        await using TestNode node = await TestNode.StartAsync("Me");
        ScaleSeed seed = ScaleData.Seed(node.Get<AppPaths>().DatabaseFile, node.Id, Small);
        IReadOnlyList<ChatMessage> all = await node.Chat.GetMessagesAsync(seed.BigPeer, Ct);
        string[] ids = [.. all.Select(m => m.Id)];

        IReadOnlyList<ChatMessage> newest = await node.Chat.GetMessagesAsync(seed.BigPeer, ChatRange.Newest(50), Ct);
        Assert.Equal(ids[^50..], newest.Select(m => m.Id));

        IReadOnlyList<ChatMessage> before = await node.Chat.GetMessagesAsync(seed.BigPeer, ChatRange.Before(Cursor(newest[0]), 50), Ct);
        Assert.Equal(ids[20..70], before.Select(m => m.Id));
        IReadOnlyList<ChatMessage> first = await node.Chat.GetMessagesAsync(seed.BigPeer, ChatRange.Before(Cursor(before[0]), 50), Ct);
        Assert.Equal(ids[..20], first.Select(m => m.Id));

        Assert.Equal(ids[11..61], (await node.Chat.GetMessagesAsync(seed.BigPeer, ChatRange.After(Cursor(all[10]), 50), Ct)).Select(m => m.Id));
        Assert.Equal(ids[30..], (await node.Chat.GetMessagesAsync(seed.BigPeer, ChatRange.Between(Cursor(all[30]), null), Ct)).Select(m => m.Id));
        Assert.Equal(ids[30..41], (await node.Chat.GetMessagesAsync(seed.BigPeer, ChatRange.Between(Cursor(all[30]), Cursor(all[40])), Ct)).Select(m => m.Id));

        // The reactions of a part are those on its messages.
        IReadOnlyList<ChatReaction> reactions = await node.Chat.GetReactionsAsync(seed.BigPeer, Ct);
        IReadOnlyList<ChatReaction> part = await node.Chat.GetReactionsAsync(seed.BigPeer, Cursor(all[30]), Cursor(all[40]), Ct);
        HashSet<string> inPart = [.. ids[30..41]];
        Assert.NotEmpty(part);
        Assert.Equal(reactions.Where(r => inPart.Contains(r.MessageId)), part);

        Assert.Equal(ids[5], (await node.Chat.GetMessageAsync(seed.BigPeer, ids[5], Ct))?.Id);
        Assert.Null(await node.Chat.GetMessageAsync(seed.BigPeer, "khong-co", Ct));
    }

    [Fact]
    public async Task A_group_conversation_is_read_a_page_at_a_time_with_how_far_ours_got()
    {
        await using TestNode node = await TestNode.StartAsync("Me");
        ScaleSeed seed = ScaleData.Seed(node.Get<AppPaths>().DatabaseFile, node.Id, Small);
        await node.GroupStore.LoadAsync(Ct);
        IReadOnlyList<GroupChatMessage> all = await node.GroupChat.GetMessagesAsync(seed.BigGroup, Ct);

        IReadOnlyList<GroupChatMessage> newest = await node.GroupChat.GetMessagesAsync(seed.BigGroup, ChatRange.Newest(50), Ct);
        IReadOnlyList<GroupChatMessage> before = await node.GroupChat.GetMessagesAsync(seed.BigGroup, ChatRange.Before(Cursor(newest[0]), 50), Ct);

        Assert.Equal(all.TakeLast(50), newest);
        Assert.Equal(all.Skip(20).Take(50), before);
        Assert.Contains(newest, m => m.Outgoing && m.State == ChatMessageState.Read);
        IReadOnlyList<ChatReaction> part = await node.GroupChat.GetReactionsAsync(seed.BigGroup, Cursor(newest[0]), null, Ct);
        HashSet<string> shown = [.. newest.Select(m => m.Id)];
        // Several at the same moment come in no particular order.
        Assert.Equal(
            (await node.GroupChat.GetReactionsAsync(seed.BigGroup, Ct)).Where(r => shown.Contains(r.MessageId)).OrderBy(r => r.MessageId).ThenBy(r => r.Reactor.Value),
            part.OrderBy(r => r.MessageId).ThenBy(r => r.Reactor.Value));
        Assert.Equal(all[7], await node.GroupChat.GetMessageAsync(seed.BigGroup, all[7].Id, Ct));
    }

    private static ChatCursor Cursor(ChatMessage message) => new(message.SentAt, message.Id);

    private static ChatCursor Cursor(GroupChatMessage message) => new(message.SentAt, message.Id);
}
