using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Chat;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// One reaction under a bubble: "👍 2", ours standing out, and who chose it.
/// </summary>
public sealed record ChatReactionChip(string Emoji, int Count, bool Mine, string Names)
{
    public string Text => $"{Emoji} {Count}";

    /// <summary>"👍 Mai, Bạn".</summary>
    public string Tip => $"{Emoji} {Names}";
}

/// <summary>
/// The notification for reactions to one of our messages (docs/18-chat-extras.md §6): one per message, listing who
/// reacted since it was last seen, each once with their newest reaction.
/// </summary>
public static class ChatReactionNotice
{
    /// <param name="reactors">Who reacted and with what, newest first.</param>
    /// <param name="words">The message, as a list shows it.</param>
    public static string Text(IReadOnlyList<(string Name, string Emoji)> reactors, string words)
    {
        string quoted = TransferText.Preview(words, 60);
        return reactors.Count switch
        {
            1 => L.F("{0} đã thả {1} vào tin của bạn: “{2}”", reactors[0].Name, reactors[0].Emoji, quoted),
            2 => L.F("{0} và {1} đã thả cảm xúc vào tin của bạn: “{2}”", reactors[0].Name, reactors[1].Name, quoted),
            _ => L.F("{0}, {1} và {2} người khác đã thả cảm xúc vào tin của bạn: “{3}”", reactors[0].Name, reactors[1].Name, reactors.Count - 2, quoted),
        };
    }

    /// <returns>Null when nothing is left to tell (seen since, or taken away).</returns>
    public static async Task<string?> ForPersonAsync(ChatService chat, DeviceId peer, string messageId, string name)
    {
        ChatReaction? newest = (await chat.GetReactionsAsync(peer, CancellationToken.None, messageId)).LastOrDefault(r => r.Unseen);
        ChatMessage? message = (await chat.GetMessagesAsync(peer, CancellationToken.None)).FirstOrDefault(m => m.Id == messageId);
        return newest is null || message is null ? null : Text([(name, newest.Emoji)], ChatBubbleViewModel.TextOf(message));
    }

    /// <param name="nameOf">A member's name.</param>
    /// <returns>Null when nothing is left to tell (seen since, or taken away).</returns>
    public static async Task<string?> ForGroupAsync(GroupChatService groupChat, string groupId, string messageId, Func<DeviceId, string> nameOf)
    {
        List<ChatReaction> unseen = [.. (await groupChat.GetReactionsAsync(groupId, CancellationToken.None, messageId)).Where(r => r.Unseen).Reverse()];
        GroupChatMessage? message = (await groupChat.GetMessagesAsync(groupId, CancellationToken.None)).FirstOrDefault(m => m.Id == messageId);
        return unseen.Count == 0 || message is null
            ? null
            : Text([.. unseen.Select(r => (nameOf(r.Reactor), r.Emoji))], ChatBubbleViewModel.TextOf(message.Kind, message.Text, message.DurationMs));
    }
}
