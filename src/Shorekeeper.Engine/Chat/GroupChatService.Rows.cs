using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;

namespace Shorekeeper.Engine.Chat;

public sealed partial class GroupChatService
{
    private class MessageRow
    {
        public string GroupId { get; init; } = "";

        public string Id { get; init; } = "";

        public string AuthorId { get; init; } = "";

        public string AuthorName { get; init; } = "";

        public byte[] AuthorKey { get; init; } = [];

        public long Outgoing { get; init; }

        public string Kind { get; init; } = "";

        public string? Text { get; init; }

        public long SentAt { get; init; }

        public string State { get; init; } = "";

        public long? DurationMs { get; init; }

        public byte[]? Wave { get; init; }

        public string? VoicePath { get; init; }

        public byte[] Sig { get; init; } = [];

        public long OutOfGroup { get; init; }

        public string? ReplyTo { get; init; }

        /// <summary>As stored (<see cref="ChatReplies.Join"/>), for passing on exactly as signed.</summary>
        public string? Mentions { get; init; }

        public long? EditedAt { get; init; }

        public byte[]? EditSig { get; init; }

        /// <summary>Ours take their state from the deliveries; with none left (everyone gone), it reached whom it could.</summary>
        public GroupChatMessage ToMessage(Dictionary<(string, string), ChatMessageState> outgoing) => new(
            GroupId, Id, DeviceId.Parse(AuthorId), AuthorName, Outgoing != 0, Kind, Text, FromMs(SentAt),
            Outgoing != 0 ? outgoing.GetValueOrDefault((GroupId, Id), ChatMessageState.Delivered) : Enum.Parse<ChatMessageState>(State, ignoreCase: true),
            (int)(DurationMs ?? 0), Wave, VoicePath, OutOfGroup != 0, ReplyTo, ChatReplies.Split(Mentions), EditedAt is { } at ? FromMs(at) : null);

        /// <summary>As signed by its author, to send or pass on: an edited one with its signed edit.</summary>
        public GroupChatMessageBody ToBody(byte[]? audio) => new(
            Id, AuthorId, AuthorName, AuthorKey, FromMs(SentAt), Kind, Sig, Text, (int?)DurationMs, Wave, audio, ReplyTo, ChatReplies.Split(Mentions),
            EditSig is null ? null : FromMs(EditedAt ?? 0), EditSig);
    }

    private sealed class ConversationRow : MessageRow
    {
        public string GroupName { get; init; } = "";

        public long Unread { get; init; }

        public long Mentioned { get; init; }
    }

    private sealed class DeliveryRow : MessageRow
    {
        public long? RefusedAt { get; init; }
    }

    private sealed class FetchRow : MessageRow
    {
        public byte[]? RecallSig { get; init; }
    }

    private sealed class EditRow
    {
        public string GroupId { get; init; } = "";

        public string Id { get; init; } = "";

        public string? Text { get; init; }

        public long? EditedAt { get; init; }

        public byte[]? EditSig { get; init; }
    }

    private sealed class IndexRow
    {
        public long Seq { get; init; }

        public string Id { get; init; } = "";

        public string AuthorId { get; init; } = "";

        public long SentAt { get; init; }

        public string Kind { get; init; } = "";

        public long? EditedAt { get; init; }
    }
}
