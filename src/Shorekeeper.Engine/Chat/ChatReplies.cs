using Shorekeeper.Core.Identity;

namespace Shorekeeper.Engine.Chat;

/// <summary>What a reply points at and whom a group message mentions (docs/18-chat-extras.md §4).</summary>
public static class ChatReplies
{
    /// <summary>A file card is answered by its offer: the same offer is "sent" on one side and "received" on the other.</summary>
    public const string OfferPrefix = "offer:";

    /// <summary>"@mọi người": every member.</summary>
    public const string Everyone = "all";

    /// <summary>More names than any group has members.</summary>
    public const int MaxMentions = 100;

    /// <summary>Nothing, a message id (32 hex digits), or <see cref="OfferPrefix"/> and an offer's.</summary>
    public static bool IsReplyTo(string? replyTo) =>
        replyTo is null
        || Guid.TryParseExact(replyTo.StartsWith(OfferPrefix, StringComparison.Ordinal) ? replyTo[OfferPrefix.Length..] : replyTo, "N", out _);

    /// <summary>Nothing, or up to <see cref="MaxMentions"/> DeviceIds or <see cref="Everyone"/>, each once.</summary>
    public static bool AreMentions(IReadOnlyList<string>? mentions) =>
        mentions is null
        || (mentions.Count is > 0 and <= MaxMentions && mentions.Distinct().Count() == mentions.Count
            && mentions.All(m => m == Everyone || DeviceId.TryParse(m, out _)));

    /// <summary>Mentions as stored: in the signed order, separated by spaces (none has one).</summary>
    public static string? Join(IReadOnlyList<string>? mentions) => mentions is { Count: > 0 } ? string.Join(' ', mentions) : null;

    public static IReadOnlyList<string>? Split(string? stored) => stored is { Length: > 0 } ? stored.Split(' ') : null;

    /// <summary><paramref name="member"/> is named, or everyone is.</summary>
    public static bool Mentions(IReadOnlyList<string>? mentions, DeviceId member) =>
        mentions is not null && (mentions.Contains(Everyone) || mentions.Contains(member.Value));
}
