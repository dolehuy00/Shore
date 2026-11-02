using Dapper;

namespace Shorekeeper.Engine.Chat;

/// <summary>Where a message is in its conversation: by time, then by id (the order messages show in).</summary>
public readonly record struct ChatCursor(DateTimeOffset SentAt, string Id);

/// <summary>
/// Which part of a conversation to read (docs/22-scale.md S1): years of messages are read a page at a time, not all at
/// once. Always oldest first.
/// </summary>
public sealed record ChatRange
{
    private enum Part
    {
        Newest,
        Before,
        After,
        Between,
    }

    private readonly Part part;
    private readonly int count;
    private readonly ChatCursor? from;
    private readonly ChatCursor? to;

    private ChatRange(Part part, int count, ChatCursor? from, ChatCursor? to)
    {
        this.part = part;
        this.count = count;
        this.from = from;
        this.to = to;
    }

    /// <summary>The newest <paramref name="count"/> messages.</summary>
    public static ChatRange Newest(int count) => new(Part.Newest, count, null, null);

    /// <summary>The <paramref name="count"/> messages just before <paramref name="at"/>, not it.</summary>
    public static ChatRange Before(ChatCursor at, int count) => new(Part.Before, count, at, null);

    /// <summary>The <paramref name="count"/> messages just after <paramref name="at"/>, not it.</summary>
    public static ChatRange After(ChatCursor at, int count) => new(Part.After, count, at, null);

    /// <summary>From <paramref name="from"/> to <paramref name="to"/>, both in; to the newest when <paramref name="to"/> is null.</summary>
    public static ChatRange Between(ChatCursor from, ChatCursor? to) => new(Part.Between, 0, from, to);

    /// <summary>
    /// The query of <paramref name="columns"/> from <paramref name="table"/> where <paramref name="where"/> (naming the
    /// conversation, with values the caller adds to <paramref name="values"/>) holds; rows come out oldest first.
    /// </summary>
    internal string Query(string table, string columns, string where, DynamicParameters values)
    {
        values.Add("fromAt", from?.SentAt.ToUnixTimeMilliseconds());
        values.Add("fromId", from?.Id);
        values.Add("toAt", to?.SentAt.ToUnixTimeMilliseconds());
        values.Add("toId", to?.Id);
        values.Add("count", count);
        string select = $"SELECT {columns} FROM {table} WHERE {where}";
        return part switch
        {
            Part.Newest => $"SELECT * FROM ({select} ORDER BY SentAt DESC, Id DESC LIMIT @count) ORDER BY SentAt, Id;",
            Part.Before =>
                $"SELECT * FROM ({select} AND (SentAt, Id) < (@fromAt, @fromId) ORDER BY SentAt DESC, Id DESC LIMIT @count) ORDER BY SentAt, Id;",
            Part.After => $"{select} AND (SentAt, Id) > (@fromAt, @fromId) ORDER BY SentAt, Id LIMIT @count;",
            _ => $"{select} AND (SentAt, Id) >= (@fromAt, @fromId) AND (@toAt IS NULL OR (SentAt, Id) <= (@toAt, @toId)) ORDER BY SentAt, Id;",
        };
    }
}
