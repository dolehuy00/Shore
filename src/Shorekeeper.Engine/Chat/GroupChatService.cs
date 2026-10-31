using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Text;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Monitoring;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Chat;

/// <summary>Where one of our group messages is with one member.</summary>
public enum GroupDeliveryState
{
    Pending,
    Delivered,
    Read,
    Failed,

    /// <summary>A version without group messages.</summary>
    Old,
}

/// <param name="Author">Who wrote it; this device when <paramref name="Outgoing"/>.</param>
/// <param name="AuthorName">The name it came with; the member list's name is better while there is one.</param>
/// <param name="State">
/// Received: unread or read. Ours, over all members: pending until it reached someone, delivered, read once someone read
/// it, failed when it reached nobody and never will (<see cref="GroupChatService.GetDeliveriesAsync"/> has each member).
/// </param>
/// <param name="OutOfGroup">Came while its author was out of the group, before this device knew (docs/16-group-chat.md §3.6).</param>
/// <param name="ReplyTo">The message answered (<see cref="ChatReplies"/>).</param>
/// <param name="Mentions">Members named with "@", or <see cref="ChatReplies.Everyone"/>.</param>
/// <param name="EditedAt">When its words last changed after it reached someone ("Đã sửa"), on the author's clock.</param>
public sealed record GroupChatMessage(
    string GroupId, string Id, DeviceId Author, string AuthorName, bool Outgoing, string Kind, string? Text, DateTimeOffset SentAt,
    ChatMessageState State, int DurationMs = 0, byte[]? Wave = null, string? VoicePath = null, bool OutOfGroup = false,
    string? ReplyTo = null, IReadOnlyList<string>? Mentions = null, DateTimeOffset? EditedAt = null);

public sealed record GroupDelivery(DeviceId Peer, GroupDeliveryState State);

/// <summary>A member's reaction in a group's conversation.</summary>
public sealed record GroupChatReaction(string GroupId, ChatReaction Reaction);

/// <summary>A group conversation in the list: its newest message and how many are unread.</summary>
/// <param name="Mentioned">Unread messages that name us (docs/18-chat-extras.md §4).</param>
public sealed record GroupChatConversation(string GroupId, string Name, GroupChatMessage Last, int Unread, int Mentioned = 0);

/// <summary>
/// Messages to a whole group (docs/16-group-chat.md §3), apart from 1-1 ones: the author sends each one to every other
/// member, now or when that member is next online, and signs it, so it stays the author's whoever passes it on. Only
/// members of that group, by this device's member list, may send or be sent to; nobody needs to be a contact.
/// </summary>
public sealed partial class GroupChatService : BackgroundService
{
    /// <summary>A member's list may lag behind the Host's for a long-poll or two: "not a member" is tried again this long.</summary>
    public static readonly TimeSpan NotMemberGrace = TimeSpan.FromMinutes(2);

    /// <summary>What members pass on to each other (docs/16-group-chat.md §3.6): this far back, and at most this many.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(30);

    public const int HistoryMessages = 1_000;

    /// <summary>Further ahead than this, a message's time is a broken clock or an attempt to stay at the bottom.</summary>
    private static readonly TimeSpan MaxAhead = TimeSpan.FromDays(1);

    private const int MaxIndexEntries = 500;
    private const int MaxFetch = 50;

    /// <summary>A fetch answer stops adding voice messages past this (always at least one message).</summary>
    private const int MaxFetchBytes = 2 * 1024 * 1024;

    /// <summary>Index and fetch calls a member may make each second.</summary>
    private const int MaxPullsPerSecond = 10;

    /// <summary>Members online are pulled from this often, besides when they come online and when groups change.</summary>
    private static readonly TimeSpan PullEvery = TimeSpan.FromMinutes(5);

    /// <summary>The next <c>Seq</c>: storing or taking back a message puts it after everything members pulled before.</summary>
    private const string NextSeq = "(SELECT COALESCE(MAX(Seq), 0) + 1 FROM ChatGroupMessages)";

    /// <summary>Deleted here: only the id stays, so nobody passes it on to us again (docs/16-group-chat.md §3.4).</summary>
    private const string Tombstone = "Deleted = 1, Text = NULL, VoicePath = NULL, DurationMs = NULL, Wave = NULL, State = 'read'";

    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private const string Columns =
        "GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, State, DurationMs, Wave, VoicePath, Sig, OutOfGroup, ReplyTo, Mentions, EditedAt, EditSig";

    private readonly Lock gate = new();
    private readonly HashSet<DeviceId> sending = [];
    private readonly HashSet<DeviceId> sendAgain = [];

    /// <summary>A voice message goes to at most this many members at once: up to 2 MB each, to a group of up to 100.</summary>
    private readonly SemaphoreSlim voiceUploads = new(4);

    /// <summary>One pull per member at a time, like sending; a call while it runs makes it pull once more when done.</summary>
    private readonly HashSet<DeviceId> pulling = [];
    private readonly HashSet<DeviceId> pullAgain = [];
    private readonly Dictionary<DeviceId, (long Second, int Count)> pullRates = [];

    private readonly SqliteDatabase database;
    private readonly AppPaths paths;
    private readonly PeerClient client;
    private readonly PeerDirectory directory;
    private readonly GroupStore groups;
    private readonly SettingsService settings;
    private readonly ShorekeeperEngine engine;
    private readonly LocalDevice device;
    private readonly ChatService chat;
    private readonly OfferService offers;
    private readonly InboxService inbox;
    private readonly UsageStatsService usage;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<GroupChatService> logger;

    public GroupChatService(
        SqliteDatabase database,
        AppPaths paths,
        PeerClient client,
        PeerDirectory directory,
        GroupStore groups,
        SettingsService settings,
        ShorekeeperEngine engine,
        LocalDevice device,
        ChatService chat,
        OfferService offers,
        InboxService inbox,
        UsageStatsService usage,
        TimeProvider timeProvider,
        ILogger<GroupChatService> logger)
    {
        this.database = database;
        this.paths = paths;
        this.client = client;
        this.directory = directory;
        this.groups = groups;
        this.settings = settings;
        this.engine = engine;
        this.device = device;
        this.chat = chat;
        this.offers = offers;
        this.inbox = inbox;
        this.usage = usage;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>The time given to the last message sent from here, in milliseconds.</summary>
    private long lastSentAt;

    /// <summary>Raised with the group whenever its conversation changes.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Raised for each new message from a member.</summary>
    public event EventHandler<GroupChatMessage>? Received;

    /// <summary>Raised when a member reacts to one of our messages, or changes their reaction (not when they take it away).</summary>
    public event EventHandler<GroupChatReaction>? Reacted;

    /// <summary>Stores the message and sends it to every other member, now or when each is next online.</summary>
    /// <exception cref="ArgumentException">Empty, or longer than <see cref="ChatService.MaxTextLength"/>.</exception>
    /// <param name="replyTo">The message answered (<see cref="ChatReplies.IsReplyTo"/>).</param>
    /// <param name="mentions">Members named with "@", or <see cref="ChatReplies.Everyone"/>.</param>
    /// <exception cref="InvalidOperationException">Not in that group, or nobody else is.</exception>
    public async Task<GroupChatMessage> SendTextAsync(
        string groupId, string text, CancellationToken cancellationToken, string? replyTo = null, IReadOnlyList<string>? mentions = null)
    {
        return await StoreAndSendAsync(groupId, ChatKinds.Text, ChatService.CheckText(text), null, replyTo, mentions is { Count: > 0 } ? mentions : null, cancellationToken);
    }

    /// <summary>Stores a recorded voice message, then sends it like a text.</summary>
    /// <exception cref="ArgumentException">Empty, too long, or not a voice message.</exception>
    /// <exception cref="InvalidOperationException">Not in that group, or nobody else is.</exception>
    public async Task<GroupChatMessage> SendVoiceAsync(string groupId, VoiceNote note, CancellationToken cancellationToken, string? replyTo = null)
    {
        if (!ChatService.IsVoice(note.Ogg, note.DurationMs, note.Wave))
        {
            throw new ArgumentException("Not a voice message that can be sent.", nameof(note));
        }

        return await StoreAndSendAsync(groupId, ChatKinds.Voice, null, note, replyTo, null, cancellationToken);
    }

    private async Task<GroupChatMessage> StoreAndSendAsync(
        string groupId, string kind, string? text, VoiceNote? note, string? replyTo, IReadOnlyList<string>? mentions, CancellationToken cancellationToken)
    {
        if (!ChatReplies.IsReplyTo(replyTo))
        {
            throw new ArgumentException("Not a message to answer.", nameof(replyTo));
        }

        if (!ChatReplies.AreMentions(mentions))
        {
            throw new ArgumentException("Not members to mention.", nameof(mentions));
        }

        DeviceId me = engine.Identity.DeviceId;
        if (!settings.Current.GroupsEnabled || groups.Get(groupId) is not { Role: not GroupRole.Pending } group)
        {
            throw new InvalidOperationException("Not in this group.");
        }

        if (!groups.CanChat(groupId))
        {
            throw new InvalidOperationException(L.T("Nhóm này chỉ nhắn được khi Host online."));
        }

        DeviceId[] recipients = [.. group.Members.Select(m => m.DeviceId).Where(id => id != me)];
        if (recipients.Length == 0)
        {
            throw new InvalidOperationException("Nobody else is in this group.");
        }

        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        DateTimeOffset sentAt = await NextSentAtAsync(connection, groupId);
        string id = Guid.CreateVersion7(sentAt).ToString("N");
        string? path = null;
        if (note is not null)
        {
            path = VoicePath(id);
            Directory.CreateDirectory(paths.ChatVoiceDirectory);
            await File.WriteAllBytesAsync(path, note.Ogg, cancellationToken);
        }

        var content = new GroupChatContent(
            groupId, id, me, sentAt.ToUnixTimeMilliseconds(), kind, text, note?.DurationMs ?? 0, note?.Wave, note?.Ogg, replyTo, mentions);
        byte[] key = engine.Identity.Certificate.PublicKey.ExportSubjectPublicKeyInfo();
        byte[] sig;
        using (ECDsa privateKey = engine.Identity.Certificate.GetECDsaPrivateKey()!)
        {
            sig = GroupChatSignature.SignMessage(privateKey, content);
        }

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            await connection.ExecuteAsync(
                $"""
                INSERT INTO ChatGroups (GroupId, Name) VALUES (@groupId, @name) ON CONFLICT (GroupId) DO UPDATE SET Name = excluded.Name;
                INSERT INTO ChatGroupMessages
                  (GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, State, DurationMs, Wave, VoicePath, Sig, Seq, ReplyTo, Mentions)
                VALUES (@groupId, @id, @me, @name2, @key, 1, @kind, @text, @sentAt, 'pending', @durationMs, @wave, @path, @sig, {NextSeq}, @replyTo, @mentions);
                """,
                new
                {
                    groupId, name = group.Name, id, me = me.Value, name2 = device.DisplayName, key, kind, text,
                    sentAt = sentAt.ToUnixTimeMilliseconds(), durationMs = note?.DurationMs, wave = note?.Wave, path, sig, replyTo,
                    mentions = ChatReplies.Join(mentions),
                },
                transaction);
            await connection.ExecuteAsync(
                "INSERT INTO ChatGroupDeliveries (GroupId, Id, PeerId, State) VALUES (@groupId, @id, @peer, 'pending');",
                recipients.Select(peer => new { groupId, id, peer = peer.Value }),
                transaction);
            await transaction.CommitAsync(cancellationToken);
        }

        Changed?.Invoke(this, groupId);
        foreach (DeviceId peer in recipients)
        {
            Kick(peer);
        }

        return new GroupChatMessage(
            groupId, id, me, device.DisplayName, true, kind, text, sentAt, ChatMessageState.Pending, note?.DurationMs ?? 0, note?.Wave, path,
            ReplyTo: replyTo, Mentions: mentions);
    }

    /// <summary>
    /// Now, but always after every message seen in the group (docs/16-group-chat.md §3.2): a reply stays below what it
    /// answers on every device even when this clock is behind its author's, and two of ours never share a millisecond.
    /// </summary>
    private async Task<DateTimeOffset> NextSentAtAsync(SqliteConnection connection, string groupId)
    {
        long seen = await connection.ExecuteScalarAsync<long?>(
            "SELECT MAX(SentAt) FROM ChatGroupMessages WHERE GroupId = @groupId;", new { groupId }) ?? 0;
        long now = Math.Max(timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), seen + 1);
        long last, next;
        do
        {
            last = Interlocked.Read(ref lastSentAt);
            next = Math.Max(now, last + 1);
        }
        while (Interlocked.CompareExchange(ref lastSentAt, next, last) != last);

        return DateTimeOffset.FromUnixTimeMilliseconds(next);
    }

    /// <summary>The members of the group besides us; null when we are not in it (left, removed, closed, or groups are off).</summary>
    public IReadOnlyList<GroupMember>? OthersIn(string groupId) =>
        settings.Current.GroupsEnabled && groups.Get(groupId) is { Role: not GroupRole.Pending } group
            ? [.. group.Members.Where(m => m.DeviceId != engine.Identity.DeviceId)]
            : null;

    /// <summary>"Tắt thông báo" (docs/16-group-chat.md §6): no toast or notification for the group, only its unread count.</summary>
    public async Task SetMutedAsync(string groupId, bool muted, CancellationToken cancellationToken)
    {
        await WriteAsync(
            """
            INSERT INTO ChatGroups (GroupId, Name, Muted) VALUES (@groupId, @name, @muted)
            ON CONFLICT (GroupId) DO UPDATE SET Muted = excluded.Muted;
            """,
            new { groupId, name = groups.Get(groupId)?.Name ?? groupId, muted },
            cancellationToken);
        Changed?.Invoke(this, groupId);
    }

    /// <summary>When each group's conversation was last deleted: its file cards up to then no longer show.</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetClearedAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string GroupId, long ClearedAt)>("SELECT GroupId, ClearedAt FROM ChatGroups WHERE ClearedAt IS NOT NULL;");
        return rows.ToDictionary(r => r.GroupId, r => FromMs(r.ClearedAt));
    }

    /// <summary>
    /// "Thu hồi" on a file card in the group's conversation (docs/16-group-chat.md §6.3), within
    /// <see cref="ChatService.RecallWindow"/> of sending or while it reached nobody: the offer is withdrawn, its card
    /// becomes a note, and each member it was for is told, now or when next online.
    /// </summary>
    /// <returns>False when it can no longer be taken back (too late, already, or not ours to this group).</returns>
    public async Task<bool> RecallOfferAsync(string groupId, string offerId, CancellationToken cancellationToken)
    {
        if (offers.GetOffers().FirstOrDefault(o => o.OfferId == offerId) is not { Recalled: false } offer || offer.GroupId != groupId
            || (timeProvider.GetUtcNow() - offer.CreatedAt > ChatService.RecallWindow && offer.Recipients.Any(r => r.State != RecipientState.Pending)))
        {
            return false;
        }

        await offers.RecallAsync(offerId);
        await WriteAsync(
            "INSERT OR IGNORE INTO ChatGroupRecalls (GroupId, PeerId, Id, Sig, IsOffer) VALUES (@groupId, @peer, @offerId, @noSig, 1);",
            offer.Recipients.Select(r => new { groupId, peer = r.DeviceId.Value, offerId, noSig = Array.Empty<byte>() }),
            cancellationToken);
        Changed?.Invoke(this, groupId);
        foreach (SentRecipient recipient in offer.Recipients)
        {
            Kick(recipient.DeviceId);
        }

        return true;
    }

    /// <summary>The groups whose notifications are off.</summary>
    public async Task<IReadOnlySet<string>> GetMutedAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        return (await connection.QueryAsync<string>("SELECT GroupId FROM ChatGroups WHERE Muted = 1;")).ToHashSet();
    }

    /// <summary>"Gửi lại" on one of our messages: to every member it failed for (or found too old) so far.</summary>
    public async Task RetryAsync(string groupId, string id, CancellationToken cancellationToken)
    {
        List<string> peers;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            peers = [.. await connection.QueryAsync<string>(
                "SELECT PeerId FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id = @id AND State IN ('failed', 'old');", new { groupId, id })];
        }

        foreach (string peer in peers)
        {
            await RetryAsync(groupId, id, DeviceId.Parse(peer), cancellationToken);
        }
    }

    /// <summary>Sends one of our messages to a member again, after it failed there.</summary>
    public async Task RetryAsync(string groupId, string id, DeviceId peer, CancellationToken cancellationToken)
    {
        if (await WriteAsync(
                """
                UPDATE ChatGroupDeliveries SET State = 'pending', RefusedAt = NULL
                WHERE GroupId = @groupId AND Id = @id AND PeerId = @peer AND State IN ('failed', 'old');
                """,
                new { groupId, id, peer = peer.Value },
                cancellationToken) > 0)
        {
            Changed?.Invoke(this, groupId);
            Kick(peer);
        }
    }

    /// <summary>
    /// Takes messages out of the conversation on this device only, with their voice files; the members keep theirs. One
    /// of ours still waiting for someone is never sent to them. Their ids stay, so no member passes them on to us again.
    /// </summary>
    public async Task DeleteAsync(string groupId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        List<string?> files;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatGroupMessages WHERE GroupId = @groupId AND Id IN @ids AND VoicePath IS NOT NULL;", new { groupId, ids })];
            await connection.ExecuteAsync(
                $"""
                UPDATE ChatGroupMessages SET {Tombstone} WHERE GroupId = @groupId AND Id IN @ids;
                DELETE FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id IN @ids;
                """,
                new { groupId, ids });
            await ChatReactions.Groups.DeleteAsync(connection, groupId, ids);
        }

        DeleteFiles(files);
        Changed?.Invoke(this, groupId);
    }

    /// <summary>
    /// The whole group conversation, on this device only. A recall not told yet stays. Members passing on messages from
    /// before now are not listened to: what was deleted stays deleted.
    /// </summary>
    public async Task DeleteConversationAsync(string groupId, CancellationToken cancellationToken)
    {
        List<string?> files;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatGroupMessages WHERE GroupId = @groupId AND VoicePath IS NOT NULL;", new { groupId })];
            await connection.ExecuteAsync(
                $"""
                UPDATE ChatGroupMessages SET {Tombstone} WHERE GroupId = @groupId;
                DELETE FROM ChatGroupDeliveries WHERE GroupId = @groupId;
                DELETE FROM ChatGroupReceipts WHERE GroupId = @groupId;
                UPDATE ChatGroups SET ClearedAt = @now WHERE GroupId = @groupId;
                """,
                new { groupId, now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds() });
            await ChatReactions.Groups.DeleteAsync(connection, groupId, null);
        }

        DeleteFiles(files);
        Changed?.Invoke(this, groupId);
    }

    /// <summary>
    /// A reaction of ours to a message of the group (docs/18-chat-extras.md §6), or none to take it away: here at once,
    /// and on each other member's device as soon as it is told. Not passed on: a member who joins later never sees it.
    /// </summary>
    /// <returns>False when the message is not one to react to (gone, taken back, a note), or we are not in the group.</returns>
    /// <exception cref="ArgumentException">Not one of <see cref="ChatReactions.Emojis"/>.</exception>
    public async Task<bool> ReactAsync(string groupId, string messageId, string? emoji, CancellationToken cancellationToken)
    {
        if (!ChatReactions.IsEmoji(emoji))
        {
            throw new ArgumentException("Not a reaction.", nameof(emoji));
        }

        DeviceId me = engine.Identity.DeviceId;
        if (OthersIn(groupId) is not { } others)
        {
            return false;
        }

        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            if (!await IsReactableAsync(connection, groupId, messageId))
            {
                return false;
            }

            long at = await ChatReactions.Groups.NextAsync(connection, groupId, messageId, me, timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await ChatReactions.Groups.StoreAsync(connection, groupId, messageId, me, emoji, at, unseen: false);
            await connection.ExecuteAsync(
                "INSERT OR IGNORE INTO ChatGroupReactionsOut (GroupId, PeerId, MessageId) VALUES (@groupId, @peer, @messageId);",
                others.Select(m => new { groupId, peer = m.DeviceId.Value, messageId }));
        }

        Changed?.Invoke(this, groupId);
        foreach (GroupMember member in others)
        {
            Kick(member.DeviceId);
        }

        return true;
    }

    /// <summary>One of ours, not a member's.</summary>
    public bool IsOurs(ChatReaction reaction) => reaction.Reactor == engine.Identity.DeviceId;

    /// <summary>The reactions in the group's conversation, oldest first.</summary>
    public async Task<IReadOnlyList<ChatReaction>> GetReactionsAsync(string groupId, CancellationToken cancellationToken, string? messageId = null)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        return await ChatReactions.Groups.GetAsync(connection, groupId, messageId);
    }

    /// <summary>The conversation is being looked at: members' reactions to ours are seen, and the next starts a notification.</summary>
    public async Task MarkReactionsSeenAsync(string groupId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        if (await ChatReactions.Groups.MarkSeenAsync(connection, groupId) > 0)
        {
            Changed?.Invoke(this, groupId);
        }
    }

    /// <summary>Words or a voice message here, neither taken back, deleted, nor from a newer version.</summary>
    private static async Task<bool> IsReactableAsync(SqliteConnection connection, string groupId, string messageId) =>
        await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM ChatGroupMessages WHERE GroupId = @groupId AND Id = @messageId AND Kind IN ('text', 'voice') AND Deleted = 0;",
            new { groupId, messageId }) > 0;

    /// <summary>
    /// "Sửa" (docs/18-chat-extras.md §5): new words for one of our text messages. One that reached nobody yet is signed
    /// again and goes with them, not marked as edited. Otherwise, within <see cref="ChatService.EditWindow"/> of sending:
    /// a signed edit, told to the members who have it and passed on with the message to all the others.
    /// </summary>
    /// <returns>False when it can no longer be edited (too late, taken back, or not one of our text messages).</returns>
    /// <exception cref="ArgumentException">Empty, or longer than <see cref="ChatService.MaxTextLength"/>.</exception>
    public async Task<bool> EditAsync(string groupId, string id, string text, CancellationToken cancellationToken)
    {
        text = ChatService.CheckText(text);
        List<string> told;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            var found = (await connection.QueryAsync<MessageRow>(
                $"SELECT {Columns} FROM ChatGroupMessages WHERE GroupId = @groupId AND Id = @id AND Outgoing = 1 AND Kind = 'text' AND Deleted = 0;",
                new { groupId, id })).ToList();
            if (found is not [var message])
            {
                return false;
            }

            told = [.. await connection.QueryAsync<string>(
                "SELECT PeerId FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id = @id AND State IN ('delivered', 'read');", new { groupId, id })];
            // Edited before: its signed edit goes with it, whoever has it now.
            bool reached = told.Count > 0 || message.EditSig is not null;
            if (reached && timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - message.SentAt > (long)ChatService.EditWindow.TotalMilliseconds)
            {
                return false;
            }

            await SetWordsAsync(connection, message, text, edited: reached);
            await connection.ExecuteAsync(
                "INSERT OR IGNORE INTO ChatGroupEdits (GroupId, PeerId, Id) VALUES (@groupId, @peer, @id);", told.Select(peer => new { groupId, peer, id }));
        }

        Changed?.Invoke(this, groupId);
        foreach (string peer in told)
        {
            Kick(DeviceId.Parse(peer));
        }

        return true;
    }

    /// <summary>
    /// New words for our <paramref name="message"/>: <paramref name="edited"/>, with a signed edit newer than any before
    /// and a new place in Seq, so members pulling learn; otherwise the message signed again, as if sent so.
    /// </summary>
    private async Task SetWordsAsync(SqliteConnection connection, MessageRow message, string text, bool edited)
    {
        var content = new GroupChatContent(
            message.GroupId, message.Id, engine.Identity.DeviceId, message.SentAt, ChatKinds.Text, text, 0, null, null,
            message.ReplyTo, ChatReplies.Split(message.Mentions));
        long editedAt = Math.Max(timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), (message.EditedAt ?? 0) + 1);
        using ECDsa privateKey = engine.Identity.Certificate.GetECDsaPrivateKey()!;
        if (edited)
        {
            await connection.ExecuteAsync(
                $"UPDATE ChatGroupMessages SET Text = @text, EditedAt = @editedAt, EditSig = @sig, Seq = {NextSeq} WHERE GroupId = @GroupId AND Id = @Id;",
                new { text, editedAt, sig = GroupChatSignature.SignEdit(privateKey, content, editedAt), message.GroupId, message.Id });
        }
        else
        {
            await connection.ExecuteAsync(
                "UPDATE ChatGroupMessages SET Text = @text, Sig = @sig WHERE GroupId = @GroupId AND Id = @Id;",
                new { text, sig = GroupChatSignature.SignMessage(privateKey, content), message.GroupId, message.Id });
        }
    }

    /// <summary>
    /// "Thu hồi": our messages become "Tin nhắn đã được thu hồi" here at once and on each member's device as soon as it is
    /// told, with a recall signed like the message. Members it has not reached yet never get it. Only within
    /// <see cref="ChatService.RecallWindow"/> of sending, unless it has reached nobody yet.
    /// </summary>
    public async Task RecallAsync(string groupId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        DeviceId me = engine.Identity.DeviceId;
        List<string?> files;
        List<(string Id, string Peer)> told;
        Dictionary<string, byte[]> sigs = [];
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            long since = (timeProvider.GetUtcNow() - ChatService.RecallWindow).ToUnixTimeMilliseconds();
            Dictionary<string, long> sentAts = (await connection.QueryAsync<(string Id, long SentAt)>(
                """
                SELECT Id, SentAt FROM ChatGroupMessages m
                WHERE GroupId = @groupId AND Id IN @ids AND Outgoing = 1 AND Kind <> 'recalled' AND Deleted = 0
                  AND (SentAt >= @since OR NOT EXISTS (
                    SELECT 1 FROM ChatGroupDeliveries d WHERE d.GroupId = m.GroupId AND d.Id = m.Id AND d.State IN ('delivered', 'read')));
                """,
                new { groupId, ids, since })).ToDictionary(r => r.Id, r => r.SentAt);
            List<string> mine = [.. sentAts.Keys];
            if (mine.Count == 0)
            {
                return;
            }

            using (ECDsa privateKey = engine.Identity.Certificate.GetECDsaPrivateKey()!)
            {
                foreach (string id in mine)
                {
                    sigs[id] = GroupChatSignature.SignRecall(privateKey, groupId, id, me, sentAts[id]);
                }
            }

            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatGroupMessages WHERE GroupId = @groupId AND Id IN @mine AND VoicePath IS NOT NULL;", new { groupId, mine })];
            told = [.. await connection.QueryAsync<(string, string)>(
                "SELECT Id, PeerId FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id IN @mine AND State IN ('delivered', 'read');",
                new { groupId, mine })];
            await using SqliteTransaction transaction = connection.BeginTransaction();
            // Members who got it some other way learn of the recall by pulling: it moves up in Seq.
            await connection.ExecuteAsync(
                $"""
                UPDATE ChatGroupMessages SET Kind = 'recalled', Text = NULL, VoicePath = NULL, DurationMs = NULL, Wave = NULL, RecallSig = @sig, Seq = {NextSeq}
                WHERE GroupId = @groupId AND Id = @id;
                DELETE FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id = @id AND State = 'pending';
                DELETE FROM ChatGroupReactions WHERE GroupId = @groupId AND MessageId = @id;
                """,
                mine.Select(id => new { groupId, id, sig = sigs[id] }),
                transaction);
            await connection.ExecuteAsync(
                "INSERT OR IGNORE INTO ChatGroupRecalls (GroupId, PeerId, Id, Sig) VALUES (@groupId, @peer, @id, @sig);",
                told.Select(t => new { groupId, peer = t.Peer, id = t.Id, sig = sigs[t.Id] }),
                transaction);
            await transaction.CommitAsync(cancellationToken);
        }

        DeleteFiles(files);
        Changed?.Invoke(this, groupId);
        foreach (string peer in told.Select(t => t.Peer).Distinct())
        {
            Kick(DeviceId.Parse(peer));
        }
    }

    /// <summary>Group conversations with at least one message, newest first.</summary>
    public async Task<IReadOnlyList<GroupChatConversation>> GetConversationsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ConversationRow>(
            $"""
            SELECT m.{Columns.Replace(", ", ", m.")}, g.Name AS GroupName,
                   (SELECT COUNT(*) FROM ChatGroupMessages u WHERE u.GroupId = m.GroupId AND u.State = 'unread') AS Unread,
                   (SELECT COUNT(*) FROM ChatGroupMessages u WHERE u.GroupId = m.GroupId AND u.State = 'unread'
                      AND (' ' || u.Mentions || ' ' LIKE '% all %' OR ' ' || u.Mentions || ' ' LIKE @me)) AS Mentioned
            FROM ChatGroupMessages m JOIN ChatGroups g ON g.GroupId = m.GroupId
            WHERE m.Id = (SELECT l.Id FROM ChatGroupMessages l WHERE l.GroupId = m.GroupId AND l.Deleted = 0 ORDER BY l.SentAt DESC, l.Id DESC LIMIT 1)
            ORDER BY m.SentAt DESC;
            """, new { me = $"% {engine.Identity.DeviceId.Value} %" })).ToList();
        Dictionary<(string, string), ChatMessageState> states = await OutgoingStatesAsync(connection, rows);
        return [.. rows.Select(r => new GroupChatConversation(
            r.GroupId, groups.Get(r.GroupId)?.Name ?? r.GroupName, r.ToMessage(states), (int)r.Unread, (int)r.Mentioned))];
    }

    /// <summary>The whole group conversation, oldest first.</summary>
    public async Task<IReadOnlyList<GroupChatMessage>> GetMessagesAsync(string groupId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<MessageRow>(
            $"SELECT {Columns} FROM ChatGroupMessages WHERE GroupId = @groupId AND Deleted = 0 ORDER BY SentAt, Id;", new { groupId })).ToList();
        Dictionary<(string, string), ChatMessageState> states = await OutgoingStatesAsync(connection, rows);
        return [.. rows.Select(r => r.ToMessage(states))];
    }

    /// <summary>A member's message that names us, or everyone (docs/18-chat-extras.md §4).</summary>
    public bool IsMentioned(GroupChatMessage message) => !message.Outgoing && ChatReplies.Mentions(message.Mentions, engine.Identity.DeviceId);

    /// <summary>
    /// Text messages of every group conversation with every word of <paramref name="query"/>, as Ctrl+K reads it, newest
    /// first (docs/18-chat-extras.md §3). Ours carry their state over all members.
    /// </summary>
    public async Task<IReadOnlyList<GroupChatMessage>> FindAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        List<MessageRow> found = [];
        await foreach (MessageRow row in connection.QueryUnbufferedAsync<MessageRow>(
            $"SELECT {Columns} FROM ChatGroupMessages WHERE Kind = 'text' AND Text IS NOT NULL AND Deleted = 0 ORDER BY SentAt DESC, Id DESC;")
            .WithCancellation(cancellationToken))
        {
            if (TextSearch.Matches(query, row.Text))
            {
                found.Add(row);
                if (found.Count == limit)
                {
                    break;
                }
            }
        }

        Dictionary<(string, string), ChatMessageState> states = await OutgoingStatesAsync(connection, found);
        return [.. found.Select(r => r.ToMessage(states))];
    }

    /// <summary>Where one of our messages is with each member it was for ("Chi tiết").</summary>
    public async Task<IReadOnlyList<GroupDelivery>> GetDeliveriesAsync(string groupId, string id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string PeerId, string State)>(
            "SELECT PeerId, State FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id = @id;", new { groupId, id });
        return [.. rows.Select(r => new GroupDelivery(DeviceId.Parse(r.PeerId), Enum.Parse<GroupDeliveryState>(r.State, ignoreCase: true)))];
    }

    /// <summary>
    /// The conversation is being looked at: what members sent is read, and each author is told how far ("Đã xem"), about
    /// their own messages only.
    /// </summary>
    public async Task MarkReadAsync(string groupId, CancellationToken cancellationToken)
    {
        List<(string Author, string Newest)> read;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            read = [.. await connection.QueryAsync<(string, string)>(
                "SELECT AuthorId, MAX(Id) FROM ChatGroupMessages WHERE GroupId = @groupId AND Outgoing = 0 AND State = 'unread' GROUP BY AuthorId;",
                new { groupId })];
            if (read.Count == 0)
            {
                return;
            }

            await connection.ExecuteAsync(
                "UPDATE ChatGroupMessages SET State = 'read' WHERE GroupId = @groupId AND Outgoing = 0 AND State = 'unread';", new { groupId });
            await connection.ExecuteAsync(
                """
                INSERT INTO ChatGroupReceipts (GroupId, AuthorId, ReadUpTo) VALUES (@groupId, @author, @newest)
                  ON CONFLICT (GroupId, AuthorId) DO UPDATE SET ReadUpTo = MAX(ReadUpTo, excluded.ReadUpTo);
                """,
                read.Select(r => new { groupId, author = r.Author, newest = r.Newest }));
        }

        Changed?.Invoke(this, groupId);
        foreach ((string author, _) in read)
        {
            Kick(DeviceId.Parse(author));
        }
    }

    /// <summary>Ours, over all members (see <see cref="GroupChatMessage.State"/>).</summary>
    private static async Task<Dictionary<(string, string), ChatMessageState>> OutgoingStatesAsync(SqliteConnection connection, IEnumerable<MessageRow> rows)
    {
        Dictionary<(string, string), ChatMessageState> states = [];
        foreach (var group in rows.Where(r => r.Outgoing != 0).GroupBy(r => r.GroupId))
        {
            string[] ids = [.. group.Select(r => r.Id)];
            var counts = await connection.QueryAsync<(string Id, string State, long Count)>(
                "SELECT Id, State, COUNT(*) FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id IN @ids GROUP BY Id, State;",
                new { groupId = group.Key, ids });
            foreach (var message in counts.GroupBy(c => c.Id))
            {
                HashSet<string> seen = [.. message.Select(c => c.State)];
                states[(group.Key, message.Key)] =
                    seen.Contains("read") ? ChatMessageState.Read
                    : seen.Contains("delivered") ? ChatMessageState.Delivered
                    : seen.Contains("pending") ? ChatMessageState.Pending
                    : ChatMessageState.Failed;
            }
        }

        return states;
    }

    private string VoicePath(string id) => Path.Combine(paths.ChatVoiceDirectory, id + ".ogg");

    private string ReceivedVoicePath(string id) => Path.Combine(paths.ChatVoiceDirectory, $"{id}-{Guid.NewGuid():N}.ogg");

    private void DeleteFiles(IEnumerable<string?> files)
    {
        foreach (string? file in files)
        {
            try
            {
                if (file is not null)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Playing right now: it stays as a stray file, which nothing points to any more.
                logger.LogDebug(ex, "Voice file {File} not deleted", file);
            }
        }
    }

    private async Task<int> WriteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(sql, parameters);
    }

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
