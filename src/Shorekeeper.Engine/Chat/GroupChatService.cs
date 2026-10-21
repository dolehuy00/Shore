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
    UsageStatsService usage,
    TimeProvider timeProvider,
    ILogger<GroupChatService> logger) : BackgroundService
{
    /// <summary>A member's list may lag behind the Host's for a long-poll or two: "not a member" is tried again this long.</summary>
    public static readonly TimeSpan NotMemberGrace = TimeSpan.FromMinutes(2);

    /// <summary>Further ahead than this, a message's time is a broken clock or an attempt to stay at the bottom.</summary>
    private static readonly TimeSpan MaxAhead = TimeSpan.FromDays(1);

    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private const string Columns =
        "GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, State, DurationMs, Wave, VoicePath, Sig";

    private readonly Lock gate = new();
    private readonly HashSet<DeviceId> sending = [];
    private readonly HashSet<DeviceId> sendAgain = [];

    /// <summary>A voice message goes to at most this many members at once: up to 2 MB each, to a group of up to 100.</summary>
    private readonly SemaphoreSlim voiceUploads = new(4);

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
                """
                INSERT INTO ChatGroups (GroupId, Name) VALUES (@groupId, @name) ON CONFLICT (GroupId) DO UPDATE SET Name = excluded.Name;
                INSERT INTO ChatGroupMessages (GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, State, DurationMs, Wave, VoicePath, Sig)
                VALUES (@groupId, @id, @me, @name2, @key, 1, @kind, @text, @sentAt, 'pending', @durationMs, @wave, @path, @sig);
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
    /// of ours still waiting for someone is never sent to them.
    /// </summary>
    public async Task DeleteAsync(string groupId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        List<string?> files;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatGroupMessages WHERE GroupId = @groupId AND Id IN @ids AND VoicePath IS NOT NULL;", new { groupId, ids })];
            await connection.ExecuteAsync(
                """
                DELETE FROM ChatGroupMessages WHERE GroupId = @groupId AND Id IN @ids;
                DELETE FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id IN @ids;
                """,
                new { groupId, ids });
        }

        DeleteFiles(files);
        Changed?.Invoke(this, groupId);
    }

    /// <summary>The whole group conversation, on this device only. A recall not told yet stays.</summary>
    public async Task DeleteConversationAsync(string groupId, CancellationToken cancellationToken)
    {
        List<string?> files;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatGroupMessages WHERE GroupId = @groupId AND VoicePath IS NOT NULL;", new { groupId })];
            await connection.ExecuteAsync(
                """
                DELETE FROM ChatGroupMessages WHERE GroupId = @groupId;
                DELETE FROM ChatGroupDeliveries WHERE GroupId = @groupId;
                DELETE FROM ChatGroupReceipts WHERE GroupId = @groupId;
                """,
                new { groupId });
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
            List<string> mine = [.. await connection.QueryAsync<string>(
                """
                SELECT Id FROM ChatGroupMessages m
                WHERE GroupId = @groupId AND Id IN @ids AND Outgoing = 1 AND Kind <> 'recalled'
                  AND (SentAt >= @since OR NOT EXISTS (
                    SELECT 1 FROM ChatGroupDeliveries d WHERE d.GroupId = m.GroupId AND d.Id = m.Id AND d.State IN ('delivered', 'read')));
                """,
                new { groupId, ids, since })];
            if (mine.Count == 0)
            {
                return;
            }

            using (ECDsa privateKey = engine.Identity.Certificate.GetECDsaPrivateKey()!)
            {
                foreach (string id in mine)
                {
                    sigs[id] = GroupChatSignature.SignRecall(privateKey, groupId, id, me);
                }
            }

            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatGroupMessages WHERE GroupId = @groupId AND Id IN @mine AND VoicePath IS NOT NULL;", new { groupId, mine })];
            told = [.. await connection.QueryAsync<(string, string)>(
                "SELECT Id, PeerId FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id IN @mine AND State IN ('delivered', 'read');",
                new { groupId, mine })];
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                """
                UPDATE ChatGroupMessages SET Kind = 'recalled', Text = NULL, VoicePath = NULL, DurationMs = NULL, Wave = NULL, RecallSig = @sig
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
            WHERE m.Id = (SELECT l.Id FROM ChatGroupMessages l WHERE l.GroupId = m.GroupId ORDER BY l.SentAt DESC, l.Id DESC LIMIT 1)
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
            $"SELECT {Columns} FROM ChatGroupMessages WHERE GroupId = @groupId ORDER BY SentAt, Id;", new { groupId })).ToList();
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

        DateTimeOffset now = timeProvider.GetUtcNow();
        string? text = body.Kind == ChatKinds.Text ? body.Text?.Trim() : null;
        bool voice = body.Kind == ChatKinds.Voice;
        if (!Guid.TryParseExact(body.Id, "N", out Guid parsed)
            || body.AuthorId != caller.Value
            || body.Kind is not { Length: > 0 and <= 32 } || body.Kind == ChatKinds.Recalled
            || (body.Kind == ChatKinds.Text && text is not { Length: > 0 and <= ChatService.MaxTextLength })
            || (voice && !ChatService.IsVoice(body.Audio, body.DurationMs ?? 0, body.Wave))
            || body.SentAt > now + MaxAhead
            || body.AuthorKey is null || body.Sig is null)
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        // Exactly as it came, kinds from newer versions included: what is checked is what the author signed.
        if (!GroupChatSignature.VerifyMessage(body.AuthorKey, body.Sig, new GroupChatContent(
                groupId, body.Id, caller, body.SentAt.ToUnixTimeMilliseconds(), body.Kind, body.Text, body.DurationMs ?? 0, body.Wave, body.Audio)))
        {
            logger.LogWarning("Group message {Id} from {Peer} has no valid signature of its author", body.Id, caller);
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        string id = parsed.ToString("N");
        string? path = null;
        if (voice)
        {
            // Written before the row, so a stored voice message always has its file; a resent one writes the same bytes.
            path = VoicePath(id);
            Directory.CreateDirectory(paths.ChatVoiceDirectory);
            await File.WriteAllBytesAsync(path, body.Audio!);
        }

        string name = body.AuthorName is { Length: > 0 } n ? n.Length > PresenceCodec.MaxNameLength ? n[..PresenceCodec.MaxNameLength] : n : caller.ShortForm;
        var message = new GroupChatMessage(
            groupId, id, caller, name, false, body.Kind, body.Text, body.SentAt, ChatMessageState.Unread,
            voice ? body.DurationMs!.Value : 0, voice ? body.Wave : null, path);
        int added = await WriteAsync(
            """
            INSERT INTO ChatGroups (GroupId, Name) VALUES (@groupId, @groupName) ON CONFLICT (GroupId) DO UPDATE SET Name = excluded.Name;
            INSERT OR IGNORE INTO ChatGroupMessages
              (GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, ReceivedAt, State, DurationMs, Wave, VoicePath, Sig)
            VALUES (@groupId, @Id, @author, @AuthorName, @key, 0, @Kind, @Text, @sentAt, @now, 'unread', @durationMs, @Wave, @VoicePath, @sig);
            """,
            new
            {
                groupId, groupName = groups.Get(groupId)!.Name, message.Id, author = caller.Value, message.AuthorName, key = body.AuthorKey,
                message.Kind, message.Text, sentAt = body.SentAt.ToUnixTimeMilliseconds(), now = now.ToUnixTimeMilliseconds(),
                durationMs = voice ? body.DurationMs : null, message.Wave, message.VoicePath, sig = body.Sig,
            },
            CancellationToken.None);

        // The first statement always changes a row; the message is new only when the second did too.
        if (added > 1)
        {
            usage.AddMessage(caller, sent: false);
            Changed?.Invoke(this, groupId);
            Received?.Invoke(this, message);
        }

        return PeerResponse.Empty(HttpStatusCode.NoContent);
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

        if (body.Recalls is not { Count: > 0 and <= ChatService.MaxRecall }
            || !body.Recalls.All(r => r is { Sig: not null } && Guid.TryParseExact(r.Id, "N", out _)))
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        List<string?> files = [];
        int changed = 0;
        await using (SqliteConnection connection = await database.OpenAsync())
        {
            foreach (GroupChatRecall recall in body.Recalls)
            {
                string id = Guid.ParseExact(recall.Id, "N").ToString("N");
                // Only the author takes a message back, by a recall signed with the key the message came with.
                var found = (await connection.QueryAsync<(string AuthorId, byte[] AuthorKey, string? VoicePath)>(
                    "SELECT AuthorId, AuthorKey, VoicePath FROM ChatGroupMessages WHERE GroupId = @groupId AND Id = @id AND Outgoing = 0 AND Kind <> 'recalled';",
                    new { groupId, id })).ToList();
                if (found is not [var message] || message.AuthorId != caller.Value
                    || !GroupChatSignature.VerifyRecall(message.AuthorKey, recall.Sig, groupId, id, caller))
                {
                    continue;
                }

                files.Add(message.VoicePath);
                // Read: nothing left to read, and its notification goes.
                changed += await connection.ExecuteAsync(
                    """
                    UPDATE ChatGroupMessages SET Kind = 'recalled', Text = NULL, VoicePath = NULL, DurationMs = NULL, Wave = NULL, State = 'read', RecallSig = @sig
                    WHERE GroupId = @groupId AND Id = @id;
                    """,
                    new { groupId, id, sig = recall.Sig });
            }
        }

        DeleteFiles(files);
        if (changed > 0)
        {
            Changed?.Invoke(this, groupId);
        }

        return PeerResponse.Empty(HttpStatusCode.NoContent);
    }

    // ───────────── Sending ─────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        directory.Changed += OnPeerChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await SendAllPendingAsync(stoppingToken);
                await Task.Delay(RetryEvery, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            directory.Changed -= OnPeerChanged;
        }
    }

    private void OnPeerChanged(object? sender, PeerChange change)
    {
        if (change.Kind != PeerChangeKind.Removed && change.Peer.State == PeerState.Online)
        {
            Kick(change.Peer.DeviceId);
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
        List<(string GroupId, string Id, byte[] Sig)> recalls;
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
            recalls = [.. await connection.QueryAsync<(string, string, byte[])>(
                "SELECT GroupId, Id, Sig FROM ChatGroupRecalls WHERE PeerId = @peer;", new { peer = peer.Value })];
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
            var recall = new GroupChatRecallBody([.. group.Take(ChatService.MaxRecall).Select(r => new GroupChatRecall(r.Id, r.Sig))]);
            if ((await PostAsync(peer, $"chat/groups/{group.Key}/recalls", recall, cancellationToken)).Taken is not null)
            {
                string[] done = [.. recall.Recalls.Select(r => r.Id)];
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
}
