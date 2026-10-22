using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
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
public sealed record GroupChatMessage(
    string GroupId, string Id, DeviceId Author, string AuthorName, bool Outgoing, string Kind, string? Text, DateTimeOffset SentAt,
    ChatMessageState State, int DurationMs = 0, byte[]? Wave = null, string? VoicePath = null);

public sealed record GroupDelivery(DeviceId Peer, GroupDeliveryState State);

/// <summary>A group conversation in the list: its newest message and how many are unread.</summary>
public sealed record GroupChatConversation(string GroupId, string Name, GroupChatMessage Last, int Unread);

/// <summary>
/// Messages to a whole group (docs/16-group-chat.md §3), apart from 1-1 ones: the author sends each one to every other
/// member, now or when that member is next online, and signs it, so it stays the author's whoever passes it on. Only
/// members of that group, by this device's member list, may send or be sent to; nobody needs to be a contact.
/// </summary>
public sealed class GroupChatService(
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
    ILogger<GroupChatService> logger) : BackgroundService
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
        "GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, State, DurationMs, Wave, VoicePath, Sig";

    private readonly Lock gate = new();
    private readonly HashSet<DeviceId> sending = [];
    private readonly HashSet<DeviceId> sendAgain = [];

    /// <summary>A voice message goes to at most this many members at once: up to 2 MB each, to a group of up to 100.</summary>
    private readonly SemaphoreSlim voiceUploads = new(4);

    /// <summary>One pull per member at a time, like sending; a call while it runs makes it pull once more when done.</summary>
    private readonly HashSet<DeviceId> pulling = [];
    private readonly HashSet<DeviceId> pullAgain = [];
    private readonly Dictionary<DeviceId, (long Second, int Count)> pullRates = [];

    /// <summary>The time given to the last message sent from here, in milliseconds.</summary>
    private long lastSentAt;

    /// <summary>Raised with the group whenever its conversation changes.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Raised for each new message from a member.</summary>
    public event EventHandler<GroupChatMessage>? Received;

    /// <summary>Stores the message and sends it to every other member, now or when each is next online.</summary>
    /// <exception cref="ArgumentException">Empty, or longer than <see cref="ChatService.MaxTextLength"/>.</exception>
    /// <exception cref="InvalidOperationException">Not in that group, or nobody else is.</exception>
    public async Task<GroupChatMessage> SendTextAsync(string groupId, string text, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Nothing to send.", nameof(text));
        }

        if (text.Length > ChatService.MaxTextLength)
        {
            throw new ArgumentException(L.F("Tin nhắn dài tối đa {0:N0} ký tự.", ChatService.MaxTextLength), nameof(text));
        }

        return await StoreAndSendAsync(groupId, ChatKinds.Text, text, null, cancellationToken);
    }

    /// <summary>Stores a recorded voice message, then sends it like a text.</summary>
    /// <exception cref="ArgumentException">Empty, too long, or not a voice message.</exception>
    /// <exception cref="InvalidOperationException">Not in that group, or nobody else is.</exception>
    public async Task<GroupChatMessage> SendVoiceAsync(string groupId, VoiceNote note, CancellationToken cancellationToken)
    {
        if (!ChatService.IsVoice(note.Ogg, note.DurationMs, note.Wave))
        {
            throw new ArgumentException("Not a voice message that can be sent.", nameof(note));
        }

        return await StoreAndSendAsync(groupId, ChatKinds.Voice, null, note, cancellationToken);
    }

    private async Task<GroupChatMessage> StoreAndSendAsync(
        string groupId, string kind, string? text, VoiceNote? note, CancellationToken cancellationToken)
    {
        DeviceId me = engine.Identity.DeviceId;
        if (!settings.Current.GroupsEnabled || groups.Get(groupId) is not { Role: not GroupRole.Pending } group)
        {
            throw new InvalidOperationException("Not in this group.");
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

        var content = new GroupChatContent(groupId, id, me, sentAt.ToUnixTimeMilliseconds(), kind, text, note?.DurationMs ?? 0, note?.Wave, note?.Ogg);
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
                INSERT INTO ChatGroupMessages (GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, State, DurationMs, Wave, VoicePath, Sig, Seq)
                VALUES (@groupId, @id, @me, @name2, @key, 1, @kind, @text, @sentAt, 'pending', @durationMs, @wave, @path, @sig, {NextSeq});
                """,
                new
                {
                    groupId, name = group.Name, id, me = me.Value, name2 = device.DisplayName, key, kind, text,
                    sentAt = sentAt.ToUnixTimeMilliseconds(), durationMs = note?.DurationMs, wave = note?.Wave, path, sig,
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
            groupId, id, me, device.DisplayName, true, kind, text, sentAt, ChatMessageState.Pending, note?.DurationMs ?? 0, note?.Wave, path);
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
        }

        DeleteFiles(files);
        Changed?.Invoke(this, groupId);
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
                   (SELECT COUNT(*) FROM ChatGroupMessages u WHERE u.GroupId = m.GroupId AND u.State = 'unread') AS Unread
            FROM ChatGroupMessages m JOIN ChatGroups g ON g.GroupId = m.GroupId
            WHERE m.Id = (SELECT l.Id FROM ChatGroupMessages l WHERE l.GroupId = m.GroupId AND l.Deleted = 0 ORDER BY l.SentAt DESC, l.Id DESC LIMIT 1)
            ORDER BY m.SentAt DESC;
            """)).ToList();
        Dictionary<(string, string), ChatMessageState> states = await OutgoingStatesAsync(connection, rows);
        return [.. rows.Select(r => new GroupChatConversation(
            r.GroupId, groups.Get(r.GroupId)?.Name ?? r.GroupName, r.ToMessage(states), (int)r.Unread))];
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

    // ───────────── Receiving (called by the API) ─────────────

    /// <summary>Who may use the group routes: members of that group (docs/16-group-chat.md §2), by our member list.</summary>
    private PeerResponse? Refusal(DeviceId caller, string groupId) =>
        !settings.Current.GroupsEnabled ? PeerResponse.Problem(HttpStatusCode.NotFound, ApiErrorCodes.NotFound)
        : !groups.IsMember(groupId, caller) ? PeerResponse.Problem(HttpStatusCode.Forbidden, ApiErrorCodes.NotMember)
        : null;

    internal async Task<PeerResponse> HandleMessageAsync(DeviceId caller, string groupId, GroupChatMessageBody body)
    {
        if (Refusal(caller, groupId) is { } refusal)
        {
            return refusal;
        }

        if (!chat.TakeRate(caller))
        {
            return PeerResponse.Problem(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited);
        }

        if (body.AuthorId != caller.Value || !IsValid(groupId, body, caller, caller))
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        await StoreReceivedAsync(groupId, body, caller, relayed: false);
        return PeerResponse.Empty(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// A well-formed message for this group signed by <paramref name="author"/>, whoever it came <paramref name="from"/>.
    /// Checked exactly as it came, kinds from newer versions included: what is checked is what the author signed.
    /// </summary>
    private bool IsValid(string groupId, GroupChatMessageBody body, DeviceId author, DeviceId from)
    {
        string? text = body.Kind == ChatKinds.Text ? body.Text?.Trim() : null;
        if (!Guid.TryParseExact(body.Id, "N", out _)
            || body.Kind is not { Length: > 0 and <= 32 } || body.Kind == ChatKinds.Recalled
            || (body.Kind == ChatKinds.Text && text is not { Length: > 0 and <= ChatService.MaxTextLength })
            || (body.Kind == ChatKinds.Voice && !ChatService.IsVoice(body.Audio, body.DurationMs ?? 0, body.Wave))
            || body.SentAt > timeProvider.GetUtcNow() + MaxAhead
            || body.AuthorKey is null || body.Sig is null)
        {
            return false;
        }

        if (!GroupChatSignature.VerifyMessage(body.AuthorKey, body.Sig, new GroupChatContent(
                groupId, body.Id, author, body.SentAt.ToUnixTimeMilliseconds(), body.Kind, body.Text, body.DurationMs ?? 0, body.Wave, body.Audio)))
        {
            logger.LogWarning("Group message {Id} from {Peer} has no valid signature of its author {Author}", body.Id, from, author);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stores a checked message, sent by its author or passed on by a member (<paramref name="relayed"/>), unless here
    /// already. One passed on from before we first pulled the group is history: read, and not announced. One from before
    /// "Xóa hội thoại" only leaves its id.
    /// </summary>
    private async Task StoreReceivedAsync(string groupId, GroupChatMessageBody body, DeviceId author, bool relayed)
    {
        string id = Guid.ParseExact(body.Id, "N").ToString("N");
        long sentAt = body.SentAt.ToUnixTimeMilliseconds();
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        bool voice = body.Kind == ChatKinds.Voice;
        await using SqliteConnection connection = await database.OpenAsync();
        if (await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM ChatGroupMessages WHERE GroupId = @groupId AND Id = @id;", new { groupId, id }) > 0)
        {
            return;
        }

        var group = (await connection.QueryAsync<(long? ClearedAt, long? HistoryBefore)>(
            "SELECT ClearedAt, HistoryBefore FROM ChatGroups WHERE GroupId = @groupId;", new { groupId })).FirstOrDefault();
        bool cleared = sentAt <= (group.ClearedAt ?? long.MinValue);
        bool history = relayed && sentAt < (group.HistoryBefore ?? long.MinValue);
        string? path = null;
        if (voice && !cleared)
        {
            // Written before the row, so a stored voice message always has its file; a resent one writes the same bytes.
            path = VoicePath(id);
            Directory.CreateDirectory(paths.ChatVoiceDirectory);
            await File.WriteAllBytesAsync(path, body.Audio!);
        }

        string name = body.AuthorName is { Length: > 0 } n ? n.Length > PresenceCodec.MaxNameLength ? n[..PresenceCodec.MaxNameLength] : n : author.ShortForm;
        var message = new GroupChatMessage(
            groupId, id, author, name, false, body.Kind, cleared ? null : body.Text, body.SentAt,
            cleared || history ? ChatMessageState.Read : ChatMessageState.Unread, cleared ? 0 : body.DurationMs ?? 0, cleared ? null : body.Wave, path);
        // As it came (duration and waveform of any kind): a member pulling it from here checks the same signature.
        int added = await connection.ExecuteAsync(
            $"""
            INSERT INTO ChatGroups (GroupId, Name) VALUES (@groupId, @groupName) ON CONFLICT (GroupId) DO UPDATE SET Name = excluded.Name;
            INSERT OR IGNORE INTO ChatGroupMessages
              (GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, ReceivedAt, State, DurationMs, Wave, VoicePath, Sig, Seq, Deleted)
            VALUES (@groupId, @Id, @author, @AuthorName, @key, 0, @Kind, @Text, @sentAt, @now, @state, @durationMs, @Wave, @VoicePath, @sig, {NextSeq}, @cleared);
            """,
            new
            {
                groupId, groupName = groups.Get(groupId)?.Name ?? groupId, message.Id, author = author.Value, message.AuthorName,
                key = body.AuthorKey, message.Kind, message.Text, sentAt, now, state = message.State == ChatMessageState.Read ? "read" : "unread",
                durationMs = cleared ? null : body.DurationMs, message.Wave, message.VoicePath, sig = body.Sig, cleared,
            });

        // The first statement always changes a row; the message is new only when the second did too.
        if (added < 2 || cleared)
        {
            return;
        }

        usage.AddMessage(author, sent: false);
        Changed?.Invoke(this, groupId);
        if (!history)
        {
            Received?.Invoke(this, message);
        }
    }

    internal async Task<PeerResponse> HandleReceiptAsync(DeviceId caller, string groupId, ChatReceiptBody body)
    {
        if (Refusal(caller, groupId) is { } refusal)
        {
            return refusal;
        }

        if (!Guid.TryParseExact(body.ReadUpTo, "N", out Guid upTo))
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        // Read before its delivery was confirmed: it has arrived all the same.
        if (await WriteAsync(
                """
                UPDATE ChatGroupDeliveries SET State = 'read'
                WHERE GroupId = @groupId AND PeerId = @peer AND Id <= @upTo AND State IN ('pending', 'delivered');
                """,
                new { groupId, peer = caller.Value, upTo = upTo.ToString("N") },
                CancellationToken.None) > 0)
        {
            Changed?.Invoke(this, groupId);
        }

        return PeerResponse.Empty(HttpStatusCode.NoContent);
    }

    internal async Task<PeerResponse> HandleRecallAsync(DeviceId caller, string groupId, GroupChatRecallBody body)
    {
        if (Refusal(caller, groupId) is { } refusal)
        {
            return refusal;
        }

        IReadOnlyList<string> offerIds = body.Offers ?? [];
        if (body.Recalls is null || body.Recalls.Count + offerIds.Count is not (> 0 and <= ChatService.MaxRecall)
            || !body.Recalls.All(r => r is { Sig: not null } && Guid.TryParseExact(r.Id, "N", out _))
            || !offerIds.All(id => Guid.TryParseExact(id, "N", out _)))
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        // Offers are the caller's own: the inbox only knows them under its id.
        if (offerIds.Count > 0)
        {
            await inbox.HandleRecalledAsync(caller, [.. offerIds.Select(id => Guid.ParseExact(id, "N").ToString("N"))]);
        }

        List<string?> files = [];
        int changed = 0;
        await using (SqliteConnection connection = await database.OpenAsync())
        {
            foreach (GroupChatRecall recall in body.Recalls)
            {
                changed += await ApplyRecallAsync(connection, groupId, Guid.ParseExact(recall.Id, "N").ToString("N"), caller, recall.Sig, files) ? 1 : 0;
            }
        }

        DeleteFiles(files);
        if (changed > 0)
        {
            Changed?.Invoke(this, groupId);
        }

        return PeerResponse.Empty(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Takes back a message here by its author's recall, signed with the key the message came with; false when it is not
    /// here, already taken back or deleted, or the recall is not the author's. Moves up in Seq, so members pulling learn.
    /// </summary>
    private static async Task<bool> ApplyRecallAsync(SqliteConnection connection, string groupId, string id, DeviceId author, byte[] sig, List<string?> files)
    {
        var found = (await connection.QueryAsync<(string AuthorId, byte[] AuthorKey, long SentAt, string? VoicePath)>(
            "SELECT AuthorId, AuthorKey, SentAt, VoicePath FROM ChatGroupMessages WHERE GroupId = @groupId AND Id = @id AND Outgoing = 0 AND Kind <> 'recalled' AND Deleted = 0;",
            new { groupId, id })).ToList();
        if (found is not [var message] || message.AuthorId != author.Value
            || !GroupChatSignature.VerifyRecall(message.AuthorKey, sig, groupId, id, author, message.SentAt))
        {
            return false;
        }

        files.Add(message.VoicePath);
        // Read: nothing left to read, and its notification goes.
        await connection.ExecuteAsync(
            $"""
            UPDATE ChatGroupMessages SET Kind = 'recalled', Text = NULL, VoicePath = NULL, DurationMs = NULL, Wave = NULL, State = 'read', RecallSig = @sig, Seq = {NextSeq}
            WHERE GroupId = @groupId AND Id = @id;
            """,
            new { groupId, id, sig });
        return true;
    }

    // ───────────── Passing on (docs/16-group-chat.md §3.6) ─────────────

    /// <summary>What we hold of the group, in the order we stored or took it back, within the history window.</summary>
    internal async Task<PeerResponse> HandleIndexAsync(DeviceId caller, string groupId, long after)
    {
        if (Refusal(caller, groupId) is { } refusal)
        {
            return refusal;
        }

        if (!TakePull(caller))
        {
            return PeerResponse.Problem(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited);
        }

        await using SqliteConnection connection = await database.OpenAsync();
        List<IndexRow> rows = [.. await connection.QueryAsync<IndexRow>(
            """
            SELECT Seq, Id, AuthorId, SentAt, Kind FROM ChatGroupMessages
            WHERE GroupId = @groupId AND Seq > @after AND Deleted = 0 AND SentAt >= @since
              AND (Kind <> 'recalled' OR RecallSig IS NOT NULL)
              AND Id IN (SELECT Id FROM ChatGroupMessages WHERE GroupId = @groupId AND Deleted = 0 ORDER BY SentAt DESC, Id DESC LIMIT @history)
            ORDER BY Seq LIMIT @take;
            """,
            new
            {
                groupId, after, since = (timeProvider.GetUtcNow() - HistoryWindow).ToUnixTimeMilliseconds(),
                history = HistoryMessages, take = MaxIndexEntries + 1,
            })];
        bool more = rows.Count > MaxIndexEntries;
        rows = [.. rows.Take(MaxIndexEntries)];
        // Past what was left out too (deleted, too old), so the next pull starts after it.
        long next = more ? rows[^1].Seq
            : Math.Max(after, await connection.ExecuteScalarAsync<long?>("SELECT MAX(Seq) FROM ChatGroupMessages WHERE GroupId = @groupId;", new { groupId }) ?? 0);
        return PeerResponse.Json(new GroupChatIndexResponse(
            [.. rows.Select(r => new GroupChatIndexEntry(r.Seq, r.Id, r.AuthorId, FromMs(r.SentAt), r.Kind == ChatKinds.Recalled))], next, more));
    }

    /// <summary>The messages asked for, as their authors signed them; those taken back as their signed recalls.</summary>
    internal async Task<PeerResponse> HandleFetchAsync(DeviceId caller, string groupId, GroupChatFetchBody body)
    {
        if (Refusal(caller, groupId) is { } refusal)
        {
            return refusal;
        }

        if (!TakePull(caller))
        {
            return PeerResponse.Problem(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited);
        }

        if (body.Ids is not { Count: > 0 and <= MaxFetch } || !body.Ids.All(id => Guid.TryParseExact(id, "N", out _)))
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        string[] ids = [.. body.Ids.Select(id => Guid.ParseExact(id, "N").ToString("N"))];
        List<FetchRow> rows;
        await using (SqliteConnection connection = await database.OpenAsync())
        {
            rows = [.. await connection.QueryAsync<FetchRow>(
                $"SELECT {Columns}, RecallSig FROM ChatGroupMessages WHERE GroupId = @groupId AND Id IN @ids AND Deleted = 0 ORDER BY SentAt, Id;",
                new { groupId, ids })];
        }

        List<GroupChatMessageBody> messages = [];
        List<GroupChatRecalledEntry> recalled = [];
        long bytes = 0;
        foreach (FetchRow row in rows)
        {
            if (row.Kind == ChatKinds.Recalled)
            {
                if (row.RecallSig is { } sig)
                {
                    recalled.Add(new GroupChatRecalledEntry(row.Id, row.AuthorId, row.AuthorName, row.AuthorKey, FromMs(row.SentAt), sig));
                }

                continue;
            }

            byte[]? audio = null;
            if (row.VoicePath is { } path)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                // Not more than this at once; the rest is asked for again.
                if (messages.Count > 0 && bytes + new FileInfo(path).Length > MaxFetchBytes)
                {
                    break;
                }

                audio = await File.ReadAllBytesAsync(path);
                bytes += audio.Length;
            }

            messages.Add(new GroupChatMessageBody(
                row.Id, row.AuthorId, row.AuthorName, row.AuthorKey, FromMs(row.SentAt), row.Kind, row.Sig, row.Text, (int?)row.DurationMs, row.Wave, audio));
        }

        return PeerResponse.Json(new GroupChatFetchResponse(messages, recalled));
    }

    /// <summary>Pulls what members online hold and we lack, in every group shared with them (docs/16-group-chat.md §3.6).</summary>
    public async Task PullAllAsync(CancellationToken cancellationToken)
    {
        foreach (PeerInfo peer in directory.Snapshot().Where(p => p.State == PeerState.Online))
        {
            await PullFromAsync(peer.DeviceId, cancellationToken);
        }
    }

    private void KickPull(DeviceId peer) => _ = PullFromAsync(peer, CancellationToken.None);

    private async Task PullFromAsync(DeviceId peer, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!pulling.Add(peer))
            {
                pullAgain.Add(peer);
                return;
            }
        }

        try
        {
            while (true)
            {
                foreach (Group group in groups.GetAll().Where(g => groups.IsMember(g.Id, peer)))
                {
                    // Not reachable: no use asking about its other groups now.
                    if (!await PullAsync(group, peer, cancellationToken))
                    {
                        break;
                    }
                }

                lock (gate)
                {
                    if (!pullAgain.Remove(peer))
                    {
                        pulling.Remove(peer);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                pulling.Remove(peer);
                pullAgain.Remove(peer);
            }

            if (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Pulling group messages from {Peer} failed", peer);
            }
        }
    }

    /// <returns>False when the member did not answer as one that passes messages on.</returns>
    private async Task<bool> PullAsync(Group group, DeviceId peer, CancellationToken cancellationToken)
    {
        long after;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            // The first pull of a group: what members pass on from before now is history.
            await connection.ExecuteAsync(
                """
                INSERT INTO ChatGroups (GroupId, Name, HistoryBefore) VALUES (@Id, @Name, @now)
                ON CONFLICT (GroupId) DO UPDATE SET HistoryBefore = COALESCE(HistoryBefore, excluded.HistoryBefore);
                """,
                new { group.Id, group.Name, now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds() });
            after = await connection.ExecuteScalarAsync<long?>(
                "SELECT After FROM ChatGroupCursors WHERE GroupId = @Id AND PeerId = @peer;", new { group.Id, peer = peer.Value }) ?? 0;
        }

        while (true)
        {
            GroupChatIndexResponse? index = await RequestAsync<GroupChatIndexResponse>(
                peer, HttpMethod.Get, $"chat/groups/{group.Id}/index?after={after}", null, cancellationToken);
            if (index is null || !await FetchAsync(group.Id, peer, await WantedAsync(group.Id, index.Entries), cancellationToken))
            {
                return false;
            }

            after = index.Next;
            await WriteAsync(
                """
                INSERT INTO ChatGroupCursors (GroupId, PeerId, After) VALUES (@Id, @peer, @after)
                ON CONFLICT (GroupId, PeerId) DO UPDATE SET After = excluded.After;
                """,
                new { group.Id, peer = peer.Value, after },
                cancellationToken);
            if (!index.More)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Of what a member holds, what we lack: messages not here (nor deleted here, nor ours, nor from before "Xóa hội
    /// thoại"), and recalls of messages we still show.
    /// </summary>
    private async Task<List<string>> WantedAsync(string groupId, IReadOnlyList<GroupChatIndexEntry> entries)
    {
        string me = engine.Identity.DeviceId.Value;
        string[] ids = [.. entries.Select(e => e.Id).Where(id => Guid.TryParseExact(id, "N", out _))];
        await using SqliteConnection connection = await database.OpenAsync();
        Dictionary<string, (string Kind, long Deleted)> here = (await connection.QueryAsync<(string Id, string Kind, long Deleted)>(
                "SELECT Id, Kind, Deleted FROM ChatGroupMessages WHERE GroupId = @groupId AND Id IN @ids;", new { groupId, ids }))
            .ToDictionary(r => r.Id, r => (r.Kind, r.Deleted));
        long clearedAt = await connection.ExecuteScalarAsync<long?>(
            "SELECT ClearedAt FROM ChatGroups WHERE GroupId = @groupId;", new { groupId }) ?? long.MinValue;
        return
        [
            .. entries.Where(e => ids.Contains(e.Id) && e.AuthorId != me && (here.TryGetValue(e.Id, out var local)
                ? local.Deleted == 0 && e.Recalled && local.Kind != ChatKinds.Recalled
                : e.SentAt.ToUnixTimeMilliseconds() > clearedAt))
                .Select(e => e.Id),
        ];
    }

    /// <returns>False when the member stopped answering; true once it gave what it still has.</returns>
    private async Task<bool> FetchAsync(string groupId, DeviceId peer, List<string> wanted, CancellationToken cancellationToken)
    {
        DeviceId me = engine.Identity.DeviceId;
        while (wanted.Count > 0)
        {
            string[] batch = [.. wanted.Take(MaxFetch)];
            GroupChatFetchResponse? answer = await RequestAsync<GroupChatFetchResponse>(
                peer, HttpMethod.Post, $"chat/groups/{groupId}/fetch", new GroupChatFetchBody(batch), cancellationToken);
            if (answer is null)
            {
                return false;
            }

            HashSet<string> got = [];
            foreach (GroupChatMessageBody message in answer.Messages.Where(m => batch.Contains(m.Id)))
            {
                got.Add(message.Id);
                if (DeviceId.TryParse(message.AuthorId, out DeviceId author) && author != me && IsValid(groupId, message, author, peer)
                    && IsAuthorMember(groupId, author, message.Id, peer))
                {
                    await StoreReceivedAsync(groupId, message, author, relayed: true);
                }
            }

            foreach (GroupChatRecalledEntry recall in answer.Recalled.Where(r => batch.Contains(r.Id)))
            {
                got.Add(recall.Id);
                if (DeviceId.TryParse(recall.AuthorId, out DeviceId author) && author != me && IsAuthorMember(groupId, author, recall.Id, peer))
                {
                    await StoreRecalledAsync(groupId, recall, author, peer);
                }
            }

            // It no longer has the rest (deleted, or a recording gone): nothing more to ask it for.
            if (got.Count == 0)
            {
                return true;
            }

            wanted.RemoveAll(got.Contains);
        }

        return true;
    }

    /// <summary>
    /// Passed on by a member, a message counts only while its author is a member (docs/16-group-chat.md §3.6). Signed
    /// is not enough: whoever left or was removed can still sign, and set any time before they left as sent.
    /// </summary>
    private bool IsAuthorMember(string groupId, DeviceId author, string id, DeviceId from)
    {
        if (groups.IsMember(groupId, author))
        {
            return true;
        }

        logger.LogWarning("Group message {Id} by {Author}, not a member, passed on by {Peer}: dropped", id, author, from);
        return false;
    }

    /// <summary>A message taken back before it reached us: only its note, if the recall is its author's.</summary>
    private async Task StoreRecalledAsync(string groupId, GroupChatRecalledEntry recall, DeviceId author, DeviceId from)
    {
        if (recall.AuthorKey is null || recall.Sig is null || !Guid.TryParseExact(recall.Id, "N", out Guid parsed)
            || !GroupChatSignature.VerifyRecall(recall.AuthorKey, recall.Sig, groupId, parsed.ToString("N"), author, recall.SentAt.ToUnixTimeMilliseconds()))
        {
            logger.LogWarning("Recall {Id} from {Peer} is not signed by its author {Author}", recall.Id, from, author);
            return;
        }

        string id = parsed.ToString("N");
        List<string?> files = [];
        bool changed;
        await using (SqliteConnection connection = await database.OpenAsync())
        {
            long clearedAt = await connection.ExecuteScalarAsync<long?>(
                "SELECT ClearedAt FROM ChatGroups WHERE GroupId = @groupId;", new { groupId }) ?? long.MinValue;
            string name = recall.AuthorName is { Length: > 0 } n ? n.Length > PresenceCodec.MaxNameLength ? n[..PresenceCodec.MaxNameLength] : n : author.ShortForm;
            // Here already with its words: taken back like a recall from the author.
            changed = await connection.ExecuteAsync(
                    $"""
                    INSERT OR IGNORE INTO ChatGroupMessages
                      (GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, SentAt, ReceivedAt, State, Sig, RecallSig, Seq, Deleted)
                    VALUES (@groupId, @id, @author, @name, @key, 0, 'recalled', @sentAt, @now, 'read', @noSig, @sig, {NextSeq}, @cleared);
                    """,
                    new
                    {
                        groupId, id, author = author.Value, name, key = recall.AuthorKey, sentAt = recall.SentAt.ToUnixTimeMilliseconds(),
                        now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), noSig = Array.Empty<byte>(), sig = recall.Sig,
                        cleared = recall.SentAt.ToUnixTimeMilliseconds() <= clearedAt,
                    }) > 0
                || await ApplyRecallAsync(connection, groupId, id, author, recall.Sig, files);
        }

        DeleteFiles(files);
        if (changed)
        {
            Changed?.Invoke(this, groupId);
        }
    }

    /// <returns>The answer; null when the member cannot be reached now or refused.</returns>
    private async Task<T?> RequestAsync<T>(DeviceId peer, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                peer,
                b => new HttpRequestMessage(method, new Uri(b, path)) { Content = body is null ? null : JsonContent.Create(body) },
                RequestTimeout,
                cancellationToken);
            return await PeerClient.ReadAsync<T>(response, cancellationToken);
        }
        catch (PeerUnreachableException)
        {
            return null;
        }
        catch (PeerApiException ex)
        {
            logger.LogDebug("{Path} refused by {Peer}: {Code}", path, peer, ex.Code);
            return null;
        }
    }

    private bool TakePull(DeviceId caller)
    {
        long second = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        lock (gate)
        {
            (long Second, int Count) rate = pullRates.TryGetValue(caller, out var r) && r.Second == second ? r : (second, 0);
            if (rate.Count >= MaxPullsPerSecond)
            {
                return false;
            }

            pullRates[caller] = (second, rate.Count + 1);
            return true;
        }
    }

    // ───────────── Sending ─────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        directory.Changed += OnPeerChanged;
        groups.Changed += OnGroupsChanged;
        try
        {
            await DropFromRemovedAsync();
            DateTimeOffset pulled = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                await SendAllPendingAsync(stoppingToken);
                if (timeProvider.GetUtcNow() - pulled >= PullEvery)
                {
                    pulled = timeProvider.GetUtcNow();
                    await PullAllAsync(stoppingToken);
                }

                await Task.Delay(RetryEvery, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            directory.Changed -= OnPeerChanged;
            groups.Changed -= OnGroupsChanged;
        }
    }

    private void OnPeerChanged(object? sender, PeerChange change)
    {
        if (change.Kind != PeerChangeKind.Removed && change.Peer.State == PeerState.Online)
        {
            Kick(change.Peer.DeviceId);
            KickPull(change.Peer.DeviceId);
        }
    }

    /// <summary>A group joined (or its members changed): what those online hold that we lack.</summary>
    private void OnGroupsChanged(object? sender, EventArgs e)
    {
        _ = PullAllAsync(CancellationToken.None);
        _ = DropFromRemovedAsync();
    }

    /// <summary>
    /// What reached us from someone after the Host removed them, while our member list did not know yet
    /// (docs/16-group-chat.md §3.6): deleted here like "Xóa". Judged by when it reached us, which they cannot set,
    /// against the removal time read in our clock (the Host's, less how far it is ahead).
    /// </summary>
    private async Task DropFromRemovedAsync()
    {
        try
        {
            foreach (Group group in groups.GetAll())
            {
                if (groups.GetRoster(group.Id) is not { Removed.Count: > 0 } roster)
                {
                    continue;
                }

                TimeSpan hostAhead = groups.GetHostOffset(group.Id);
                List<string> ids = [];
                await using (SqliteConnection connection = await database.OpenAsync())
                {
                    foreach (GroupRosterRemoved removed in roster.Removed)
                    {
                        ids.AddRange(await connection.QueryAsync<string>(
                            """
                            SELECT Id FROM ChatGroupMessages
                            WHERE GroupId = @groupId AND AuthorId = @author AND Outgoing = 0 AND Deleted = 0 AND ReceivedAt > @at;
                            """,
                            new { groupId = group.Id, author = removed.DeviceId, at = (removed.At - hostAhead).ToUnixTimeMilliseconds() }));
                    }
                }

                if (ids.Count > 0)
                {
                    logger.LogWarning("Deleting {Count} group messages of {Group} that came after their author was removed", ids.Count, group.Id);
                    await DeleteAsync(group.Id, ids, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deleting group messages from removed members failed");
        }
    }

    private async Task SendAllPendingAsync(CancellationToken cancellationToken)
    {
        IEnumerable<string> waiting;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            waiting = await connection.QueryAsync<string>(
                """
                SELECT PeerId FROM ChatGroupDeliveries WHERE State = 'pending'
                UNION SELECT AuthorId FROM ChatGroupReceipts UNION SELECT PeerId FROM ChatGroupRecalls;
                """);
        }

        HashSet<string> online = [.. directory.Snapshot().Where(p => p.State == PeerState.Online).Select(p => p.DeviceId.Value)];
        foreach (string peer in waiting.Where(online.Contains))
        {
            await SendPendingAsync(DeviceId.Parse(peer), cancellationToken);
        }
    }

    private void Kick(DeviceId peer) => _ = SendPendingAsync(peer, CancellationToken.None);

    /// <summary>One sender per member keeps the order; a call while it runs makes it look once more when done.</summary>
    private async Task SendPendingAsync(DeviceId peer, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!sending.Add(peer))
            {
                sendAgain.Add(peer);
                return;
            }
        }

        try
        {
            while (true)
            {
                await SendOnceAsync(peer, cancellationToken);
                lock (gate)
                {
                    if (!sendAgain.Remove(peer))
                    {
                        sending.Remove(peer);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                sending.Remove(peer);
                sendAgain.Remove(peer);
            }

            if (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sending group messages to {Peer} failed", peer);
            }
        }
    }

    private async Task SendOnceAsync(DeviceId peer, CancellationToken cancellationToken)
    {
        List<DeliveryRow> pending;
        List<(string GroupId, string ReadUpTo)> receipts;
        List<(string GroupId, string Id, byte[] Sig, long IsOffer)> recalls;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            pending = [.. await connection.QueryAsync<DeliveryRow>(
                $"""
                SELECT m.{Columns.Replace(", ", ", m.")}, d.RefusedAt
                FROM ChatGroupDeliveries d JOIN ChatGroupMessages m ON m.GroupId = d.GroupId AND m.Id = d.Id
                WHERE d.PeerId = @peer AND d.State = 'pending' AND m.Kind <> 'recalled'
                ORDER BY m.SentAt, m.Id;
                """,
                new { peer = peer.Value })];
            receipts = [.. await connection.QueryAsync<(string, string)>(
                "SELECT GroupId, ReadUpTo FROM ChatGroupReceipts WHERE AuthorId = @peer;", new { peer = peer.Value })];
            recalls = [.. await connection.QueryAsync<(string, string, byte[], long)>(
                "SELECT GroupId, Id, Sig, IsOffer FROM ChatGroupRecalls WHERE PeerId = @peer;", new { peer = peer.Value })];
        }

        // Heard directly without the capability: a version without group messages, which would only refuse them.
        bool old = directory.Snapshot().FirstOrDefault(p => p.DeviceId == peer) is { Source: PeerSource.Direct, DiscoveryPort: > 0, CanGroupChat: false };
        foreach (DeliveryRow row in pending)
        {
            // Left, removed, or the group is gone (closed, we left, IT turned groups off): never for them now.
            if (!groups.IsMember(row.GroupId, peer))
            {
                await SetDeliveryAsync(row, peer, null, cancellationToken);
                continue;
            }

            if (old)
            {
                await SetDeliveryAsync(row, peer, GroupDeliveryState.Old, cancellationToken);
                continue;
            }

            byte[]? audio = row.VoicePath is { } path && File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
            if (row.Kind == ChatKinds.Voice && audio is null)
            {
                // The recording is gone (data folder cleaned): nothing to send.
                await SetDeliveryAsync(row, peer, GroupDeliveryState.Failed, cancellationToken);
                continue;
            }

            var body = new GroupChatMessageBody(
                row.Id, row.AuthorId, row.AuthorName, row.AuthorKey, FromMs(row.SentAt), row.Kind, row.Sig, row.Text, (int?)row.DurationMs, row.Wave, audio);
            bool? taken;
            string? code;
            if (audio is null)
            {
                (taken, code) = await PostAsync(peer, $"chat/groups/{row.GroupId}/messages", body, cancellationToken);
            }
            else
            {
                await voiceUploads.WaitAsync(cancellationToken);
                try
                {
                    (taken, code) = await PostAsync(peer, $"chat/groups/{row.GroupId}/messages", body, cancellationToken);
                }
                finally
                {
                    voiceUploads.Release();
                }
            }

            if (taken is null)
            {
                return;
            }

            if (taken.Value)
            {
                await SetDeliveryAsync(row, peer, GroupDeliveryState.Delivered, cancellationToken);
            }
            else if (code == ApiErrorCodes.NotMember)
            {
                // Its member list may not have us yet: again later, in order, until the grace is over.
                long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                if (row.RefusedAt is null)
                {
                    await WriteAsync(
                        "UPDATE ChatGroupDeliveries SET RefusedAt = @now WHERE GroupId = @GroupId AND Id = @Id AND PeerId = @peer;",
                        new { now, row.GroupId, row.Id, peer = peer.Value },
                        cancellationToken);
                }

                if (row.RefusedAt is null || now - row.RefusedAt.Value < (long)NotMemberGrace.TotalMilliseconds)
                {
                    return;
                }

                await SetDeliveryAsync(row, peer, GroupDeliveryState.Failed, cancellationToken);
            }
            else
            {
                // An unknown route answers like an untrusted caller: a version without group messages.
                await SetDeliveryAsync(row, peer, code == ApiErrorCodes.NotTrusted ? GroupDeliveryState.Old : GroupDeliveryState.Failed, cancellationToken);
            }
        }

        // Refused or not, a receipt or recall is not worth keeping once the member has answered.
        foreach ((string groupId, string readUpTo) in receipts)
        {
            if ((await PostAsync(peer, $"chat/groups/{groupId}/receipts", new ChatReceiptBody(readUpTo), cancellationToken)).Taken is not null)
            {
                await WriteAsync(
                    "DELETE FROM ChatGroupReceipts WHERE GroupId = @groupId AND AuthorId = @peer AND ReadUpTo = @readUpTo;",
                    new { groupId, peer = peer.Value, readUpTo },
                    cancellationToken);
            }
        }

        foreach (var group in recalls.GroupBy(r => r.GroupId))
        {
            var batch = group.Take(ChatService.MaxRecall).ToList();
            string[] offerIds = [.. batch.Where(r => r.IsOffer != 0).Select(r => r.Id)];
            var recall = new GroupChatRecallBody(
                [.. batch.Where(r => r.IsOffer == 0).Select(r => new GroupChatRecall(r.Id, r.Sig))], offerIds.Length > 0 ? offerIds : null);
            if ((await PostAsync(peer, $"chat/groups/{group.Key}/recalls", recall, cancellationToken)).Taken is not null)
            {
                string[] done = [.. batch.Select(r => r.Id)];
                await WriteAsync(
                    "DELETE FROM ChatGroupRecalls WHERE GroupId = @groupId AND PeerId = @peer AND Id IN @done;",
                    new { groupId = group.Key, peer = peer.Value, done },
                    cancellationToken);
            }
        }
    }

    /// <summary>Where the message now is with <paramref name="peer"/>; null drops it, never to be sent there.</summary>
    private async Task SetDeliveryAsync(DeliveryRow row, DeviceId peer, GroupDeliveryState? state, CancellationToken cancellationToken)
    {
        bool first = false;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            if (state is null)
            {
                await connection.ExecuteAsync(
                    "DELETE FROM ChatGroupDeliveries WHERE GroupId = @GroupId AND Id = @Id AND PeerId = @peer;", new { row.GroupId, row.Id, peer = peer.Value });
            }
            else
            {
                // Counted once however many members it reaches (docs/16-group-chat.md §3.4).
                first = state == GroupDeliveryState.Delivered && await connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM ChatGroupDeliveries WHERE GroupId = @GroupId AND Id = @Id AND State IN ('delivered', 'read');",
                    new { row.GroupId, row.Id }) == 0;
                // A receipt may have come first and made it read.
                await connection.ExecuteAsync(
                    "UPDATE ChatGroupDeliveries SET State = @state WHERE GroupId = @GroupId AND Id = @Id AND PeerId = @peer AND State = 'pending';",
                    new { state = state.Value.ToString().ToLowerInvariant(), row.GroupId, row.Id, peer = peer.Value });
            }
        }

        if (first)
        {
            usage.AddMessage(peer, sent: true);
        }

        Changed?.Invoke(this, row.GroupId);
    }

    /// <returns>Taken: true when taken, false when refused (with its code), null to try later.</returns>
    private async Task<(bool? Taken, string? Code)> PostAsync<T>(DeviceId peer, string path, T body, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                peer, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, path)) { Content = JsonContent.Create(body) }, RequestTimeout, cancellationToken);
            await PeerClient.EnsureSuccessAsync(response, cancellationToken);
            return (true, null);
        }
        catch (PeerUnreachableException)
        {
            logger.LogDebug("Group messages to {Peer} wait: not reachable", peer);
            return (null, null);
        }
        catch (PeerApiException ex) when (ex.Status is (int)HttpStatusCode.TooManyRequests or >= 500)
        {
            return (null, null);
        }
        catch (PeerApiException ex)
        {
            logger.LogWarning("{Path} refused by {Peer}: {Code}", path, peer, ex.Code);
            return (false, ex.Code);
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

        /// <summary>Ours take their state from the deliveries; with none left (everyone gone), it reached whom it could.</summary>
        public GroupChatMessage ToMessage(Dictionary<(string, string), ChatMessageState> outgoing) => new(
            GroupId, Id, DeviceId.Parse(AuthorId), AuthorName, Outgoing != 0, Kind, Text, FromMs(SentAt),
            Outgoing != 0 ? outgoing.GetValueOrDefault((GroupId, Id), ChatMessageState.Delivered) : Enum.Parse<ChatMessageState>(State, ignoreCase: true),
            (int)(DurationMs ?? 0), Wave, VoicePath);
    }

    private sealed class ConversationRow : MessageRow
    {
        public string GroupName { get; init; } = "";

        public long Unread { get; init; }
    }

    private sealed class DeliveryRow : MessageRow
    {
        public long? RefusedAt { get; init; }
    }

    private sealed class FetchRow : MessageRow
    {
        public byte[]? RecallSig { get; init; }
    }

    private sealed class IndexRow
    {
        public long Seq { get; init; }

        public string Id { get; init; } = "";

        public string AuthorId { get; init; } = "";

        public long SentAt { get; init; }

        public string Kind { get; init; } = "";
    }
}
