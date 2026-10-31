using Dapper;
using Microsoft.Data.Sqlite;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Engine.Chat;

/// <summary>Someone's reaction to a message (docs/18-chat-extras.md §6).</summary>
/// <param name="Unseen">Someone else's on our message, not seen yet: in its notification.</param>
public sealed record ChatReaction(string MessageId, DeviceId Reactor, string Emoji, DateTimeOffset At, bool Unseen);

/// <summary>
/// The fixed set of reactions, and where a conversation's are kept: one per person and message, the newest by its
/// time; one taken away stays as a row without an emoji, so an older one arriving late cannot bring it back.
/// </summary>
public sealed class ChatReactions
{
    private readonly string table;
    private readonly string scope;

    private ChatReactions(string table, string scope)
    {
        this.table = table;
        this.scope = scope;
    }

    /// <summary>👍 ❤️ 😂 😮 😢 🙏 (docs/18-chat-extras.md §8 Q2).</summary>
    public static readonly IReadOnlyList<string> Emojis = ["👍", "❤️", "😂", "😮", "😢", "🙏"];

    /// <summary>1-1 conversations, by the peer.</summary>
    internal static readonly ChatReactions OneToOne = new("ChatReactions", "PeerId");

    /// <summary>Group conversations, by the group.</summary>
    internal static readonly ChatReactions Groups = new("ChatGroupReactions", "GroupId");

    /// <summary>One of the set, or none: taking a reaction away.</summary>
    public static bool IsEmoji(string? emoji) => emoji is null || Emojis.Contains(emoji);

    /// <returns>False when one as new or newer is here already.</returns>
    internal async Task<bool> StoreAsync(
        SqliteConnection connection, string at, string messageId, DeviceId reactor, string? emoji, long time, bool unseen) =>
        await connection.ExecuteAsync(
            $"""
            INSERT INTO {table} ({scope}, MessageId, ReactorId, Emoji, At, Unseen) VALUES (@at, @messageId, @reactor, @emoji, @time, @unseen)
            ON CONFLICT ({scope}, MessageId, ReactorId) DO UPDATE SET Emoji = excluded.Emoji, At = excluded.At, Unseen = excluded.Unseen
            WHERE excluded.At > {table}.At;
            """,
            new { at, messageId, reactor = reactor.Value, emoji, time, unseen }) > 0;

    /// <summary>The time for a new reaction of <paramref name="reactor"/>: now, but after the one before.</summary>
    internal async Task<long> NextAsync(SqliteConnection connection, string at, string messageId, DeviceId reactor, long now) =>
        Math.Max(now, (await connection.ExecuteScalarAsync<long?>(
            $"SELECT At FROM {table} WHERE {scope} = @at AND MessageId = @messageId AND ReactorId = @reactor;",
            new { at, messageId, reactor = reactor.Value }) ?? 0) + 1);

    /// <summary>The reactions in a conversation, oldest first.</summary>
    internal async Task<IReadOnlyList<ChatReaction>> GetAsync(SqliteConnection connection, string at, string? messageId = null)
    {
        var rows = await connection.QueryAsync<(string MessageId, string ReactorId, string Emoji, long At, long Unseen)>(
            $"""
            SELECT MessageId, ReactorId, Emoji, At, Unseen FROM {table}
            WHERE {scope} = @at AND Emoji IS NOT NULL AND (@messageId IS NULL OR MessageId = @messageId) ORDER BY At;
            """,
            new { at, messageId });
        return [.. rows.Select(r => new ChatReaction(r.MessageId, DeviceId.Parse(r.ReactorId), r.Emoji, DateTimeOffset.FromUnixTimeMilliseconds(r.At), r.Unseen != 0))];
    }

    /// <summary>What a reaction of ours tells: its emoji (none when taken away) and time.</summary>
    internal async Task<(string? Emoji, long At)?> MineAsync(SqliteConnection connection, string at, string messageId, DeviceId me) =>
        (await connection.QueryAsync<(string?, long)>(
            $"SELECT Emoji, At FROM {table} WHERE {scope} = @at AND MessageId = @messageId AND ReactorId = @me;",
            new { at, messageId, me = me.Value })).Select(r => ((string?, long)?)r).FirstOrDefault();

    /// <returns>How many were unseen.</returns>
    internal Task<int> MarkSeenAsync(SqliteConnection connection, string at) =>
        connection.ExecuteAsync($"UPDATE {table} SET Unseen = 0 WHERE {scope} = @at AND Unseen = 1;", new { at });

    /// <summary>Messages taken back or deleted: their reactions go with them.</summary>
    internal Task DeleteAsync(SqliteConnection connection, string at, IEnumerable<string>? messageIds) =>
        connection.ExecuteAsync(
            $"DELETE FROM {table} WHERE {scope} = @at AND (@all OR MessageId IN @ids);",
            new { at, all = messageIds is null, ids = messageIds ?? [] });
}
