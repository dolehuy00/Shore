using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Monitoring;
using Shorekeeper.Engine.Storage;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Chat;

/// <summary>Sent: pending → delivered → read, or failed. Received: unread → read.</summary>
public enum ChatMessageState
{
    Pending,
    Delivered,
    Read,
    Failed,
    Unread,
}

/// <param name="Kind">A <see cref="ChatKinds"/> value, or one from a newer version, which has no text.</param>
/// <param name="SentAt">On the sender's clock (received ones never later than when they came): it orders the conversation.</param>
/// <param name="VoicePath">A voice message's Ogg Opus file; <paramref name="DurationMs"/> and <paramref name="Wave"/> come with it.</param>
public sealed record ChatMessage(
    DeviceId Peer, string Id, bool Outgoing, string Kind, string? Text, DateTimeOffset SentAt, ChatMessageState State,
    int DurationMs = 0, byte[]? Wave = null, string? VoicePath = null);

/// <summary>A conversation in the list: its newest message and how many from the peer are unread.</summary>
public sealed record ChatConversation(DeviceId Peer, ChatMessage Last, int Unread);

/// <summary>
/// Messages between two contacts (docs/15-chat.md §2), apart from offers. Each is stored first, then sent; one that
/// cannot reach the peer waits and goes out, in order, when the peer is seen online. Ids are made by the sender, so
/// sending again is harmless.
/// </summary>
public sealed class ChatService(
    SqliteDatabase database,
    AppPaths paths,
    PeerClient client,
    PeerDirectory directory,
    UsageStatsService usage,
    OfferService offers,
    InboxService inbox,
    TimeProvider timeProvider,
    ILogger<ChatService> logger) : BackgroundService
{
    public const int MaxTextLength = 4_000;

    /// <summary>A voice message's file: five minutes at 24 kbps is under 1 MB.</summary>
    public const int MaxVoiceBytes = 2 * 1024 * 1024;

    /// <summary>Messages a peer may send us each second.</summary>
    public const int MaxPerSecond = 20;

    /// <summary>Messages one recall may take back.</summary>
    public const int MaxRecall = 500;

    /// <summary>How long after sending a message or file can be taken back ("Thu hồi"); one not delivered yet, any time.</summary>
    public static readonly TimeSpan RecallWindow = TimeSpan.FromMinutes(5);

    /// <summary>Waiting messages are tried again this often while their peer is online (it may have been busy).</summary>
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private const string Columns = "PeerId, Id, Outgoing, Kind, Text, SentAt, State, DurationMs, Wave, VoicePath";

    private readonly Lock gate = new();
    private readonly HashSet<DeviceId> sending = [];
    private readonly HashSet<DeviceId> sendAgain = [];
    private readonly Dictionary<DeviceId, (long Second, int Count)> rates = [];

    /// <summary>The time given to the last message sent from here, in milliseconds.</summary>
    private long lastSentAt;

    /// <summary>Raised with the peer whenever its conversation changes.</summary>
    public event EventHandler<DeviceId>? Changed;

    /// <summary>Raised for each new message from a peer.</summary>
    public event EventHandler<ChatMessage>? Received;

    /// <summary>Stores the message and sends it, now or when the peer is next online.</summary>
    /// <exception cref="ArgumentException">Empty, or longer than <see cref="MaxTextLength"/>.</exception>
    public async Task<ChatMessage> SendTextAsync(DeviceId peer, string text, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Nothing to send.", nameof(text));
        }

        if (text.Length > MaxTextLength)
        {
            throw new ArgumentException(L.F("Tin nhắn dài tối đa {0:N0} ký tự.", MaxTextLength), nameof(text));
        }

        DateTimeOffset now = NextSentAt();
        var message = new ChatMessage(
            peer, Guid.CreateVersion7(now).ToString("N"), true, ChatKinds.Text, text, FromMs(now.ToUnixTimeMilliseconds()), ChatMessageState.Pending);
        await WriteAsync(
            "INSERT INTO ChatMessages (PeerId, Id, Outgoing, Kind, Text, SentAt, State) VALUES (@peer, @Id, 1, @Kind, @Text, @sentAt, 'pending');",
            new { peer = peer.Value, message.Id, message.Kind, message.Text, sentAt = now.ToUnixTimeMilliseconds() },
            cancellationToken);

        Changed?.Invoke(this, peer);
        Kick(peer);
        return message;
    }

    /// <summary>
    /// Now, but always after the message sent before: messages are ordered by this time, then by id, and a UUIDv7 is
    /// random within its millisecond, so two sent in the same one could swap places on both sides.
    /// </summary>
    private DateTimeOffset NextSentAt()
    {
        long now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long last, next;
        do
        {
            last = Interlocked.Read(ref lastSentAt);
            next = Math.Max(now, last + 1);
        }
        while (Interlocked.CompareExchange(ref lastSentAt, next, last) != last);

        return DateTimeOffset.FromUnixTimeMilliseconds(next);
    }

    /// <summary>Stores a recorded voice message, then sends it like a text.</summary>
    /// <exception cref="ArgumentException">Empty, too long, or not a voice message.</exception>
    public async Task<ChatMessage> SendVoiceAsync(DeviceId peer, VoiceNote note, CancellationToken cancellationToken)
    {
        if (!IsVoice(note.Ogg, note.DurationMs, note.Wave))
        {
            throw new ArgumentException("Not a voice message that can be sent.", nameof(note));
        }

        DateTimeOffset now = NextSentAt();
        string id = Guid.CreateVersion7(now).ToString("N");
        string path = VoicePath(id);
        Directory.CreateDirectory(paths.ChatVoiceDirectory);
        await File.WriteAllBytesAsync(path, note.Ogg, cancellationToken);
        var message = new ChatMessage(
            peer, id, true, ChatKinds.Voice, null, FromMs(now.ToUnixTimeMilliseconds()), ChatMessageState.Pending, note.DurationMs, note.Wave, path);
        await WriteAsync(
            """
            INSERT INTO ChatMessages (PeerId, Id, Outgoing, Kind, SentAt, State, DurationMs, Wave, VoicePath)
            VALUES (@peer, @Id, 1, @Kind, @sentAt, 'pending', @DurationMs, @Wave, @VoicePath);
            """,
            new { peer = peer.Value, message.Id, message.Kind, sentAt = now.ToUnixTimeMilliseconds(), message.DurationMs, message.Wave, message.VoicePath },
            cancellationToken);

        Changed?.Invoke(this, peer);
        Kick(peer);
        return message;
    }

    /// <summary>Sends a failed message again.</summary>
    public async Task RetryAsync(DeviceId peer, string id, CancellationToken cancellationToken)
    {
        if (await WriteAsync(
                "UPDATE ChatMessages SET State = 'pending' WHERE PeerId = @peer AND Id = @id AND Outgoing = 1 AND State = 'failed';",
                new { peer = peer.Value, id },
                cancellationToken) > 0)
        {
            Changed?.Invoke(this, peer);
            Kick(peer);
        }
    }

    /// <summary>
    /// Takes messages out of the conversation on this device only (docs/15-chat.md §2), with their voice files; the peer
    /// keeps its copy. A sent message still waiting goes too, so it is never sent.
    /// </summary>
    public async Task DeleteAsync(DeviceId peer, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        List<string?> files;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatMessages WHERE PeerId = @peer AND Id IN @ids AND VoicePath IS NOT NULL;", new { peer = peer.Value, ids })];
            await connection.ExecuteAsync("DELETE FROM ChatMessages WHERE PeerId = @peer AND Id IN @ids;", new { peer = peer.Value, ids });
        }

        DeleteFiles(files);
        Changed?.Invoke(this, peer);
    }

    /// <summary>
    /// "Thu hồi": our messages become "Tin nhắn đã được thu hồi" here at once, their words and voice gone, and on the
    /// peer's device as soon as it is told (now, or when it is next online). One still waiting is never sent. Only within
    /// <see cref="RecallWindow"/> of sending, unless still waiting.
    /// </summary>
    public async Task RecallAsync(DeviceId peer, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        List<string?> files;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            long since = (timeProvider.GetUtcNow() - RecallWindow).ToUnixTimeMilliseconds();
            List<string> mine = [.. await connection.QueryAsync<string>(
                """
                SELECT Id FROM ChatMessages
                WHERE PeerId = @peer AND Id IN @ids AND Outgoing = 1 AND Kind <> 'recalled' AND (SentAt >= @since OR State = 'pending');
                """,
                new { peer = peer.Value, ids, since })];
            if (mine.Count == 0)
            {
                return;
            }

            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatMessages WHERE PeerId = @peer AND Id IN @mine AND VoicePath IS NOT NULL;", new { peer = peer.Value, mine })];
            await connection.ExecuteAsync(
                "UPDATE ChatMessages SET Kind = 'recalled', Text = NULL, VoicePath = NULL, DurationMs = NULL, Wave = NULL WHERE PeerId = @peer AND Id IN @mine;",
                new { peer = peer.Value, mine });
            await connection.ExecuteAsync(
                "INSERT OR IGNORE INTO ChatRecalls (PeerId, Id) VALUES (@peer, @id);", mine.Select(id => new { peer = peer.Value, id }));
        }

        DeleteFiles(files);
        Changed?.Invoke(this, peer);
        Kick(peer);
    }

    /// <summary>
    /// "Thu hồi" on a file card (docs/15-chat.md §6.3), within <see cref="RecallWindow"/> of sending or while not yet
    /// delivered: the offer is withdrawn, its card becomes a note, and every recipient is told like for a message.
    /// </summary>
    /// <returns>False when it can no longer be taken back (too late, already, or not one of ours to this peer).</returns>
    public async Task<bool> RecallOfferAsync(DeviceId peer, string offerId, CancellationToken cancellationToken)
    {
        if (offers.GetOffers().FirstOrDefault(o => o.OfferId == offerId) is not { GroupId: null, Recalled: false } offer
            || offer.Recipients.FirstOrDefault(r => r.DeviceId == peer) is not { } recipient
            || (timeProvider.GetUtcNow() - offer.CreatedAt > RecallWindow && recipient.State != RecipientState.Pending))
        {
            return false;
        }

        await offers.RecallAsync(offerId);
        await WriteAsync(
            "INSERT OR IGNORE INTO ChatRecalls (PeerId, Id, IsOffer) VALUES (@peer, @offerId, 1);",
            offer.Recipients.Select(r => new { peer = r.DeviceId.Value, offerId }),
            cancellationToken);
        foreach (SentRecipient r in offer.Recipients)
        {
            Changed?.Invoke(this, r.DeviceId);
            Kick(r.DeviceId);
        }

        return true;
    }

    /// <summary>
    /// The whole conversation, on this device only: messages, voice files, a "Đã xem" not told yet, and the file cards so
    /// far (<see cref="GetClearedAsync"/>; the offers stay in Hộp nhận and Đã gửi).
    /// </summary>
    public async Task DeleteConversationAsync(DeviceId peer, CancellationToken cancellationToken)
    {
        List<string?> files;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatMessages WHERE PeerId = @peer AND VoicePath IS NOT NULL;", new { peer = peer.Value })];
            // A recall not told yet stays: deleting here does not take back what the peer has.
            await connection.ExecuteAsync(
                """
                DELETE FROM ChatMessages WHERE PeerId = @peer; DELETE FROM ChatReceipts WHERE PeerId = @peer;
                INSERT INTO ChatCleared (PeerId, ClearedAt) VALUES (@peer, @at)
                ON CONFLICT (PeerId) DO UPDATE SET ClearedAt = excluded.ClearedAt;
                """,
                new { peer = peer.Value, at = timeProvider.GetUtcNow().ToUnixTimeMilliseconds() });
        }

        DeleteFiles(files);
        Changed?.Invoke(this, peer);
    }

    /// <summary>
    /// When each conversation was last deleted: file cards sent before then, or received before then, no longer show in it.
    /// </summary>
    public async Task<IReadOnlyDictionary<DeviceId, DateTimeOffset>> GetClearedAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string PeerId, long ClearedAt)>("SELECT PeerId, ClearedAt FROM ChatCleared;");
        return rows.ToDictionary(r => DeviceId.Parse(r.PeerId), r => FromMs(r.ClearedAt));
    }

    /// <summary>Newest first.</summary>
    public async Task<IReadOnlyList<ChatConversation>> GetConversationsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ConversationRow>(
            $"""
            SELECT {Columns},
                   (SELECT COUNT(*) FROM ChatMessages u WHERE u.PeerId = m.PeerId AND u.State = 'unread') AS Unread
            FROM ChatMessages m
            WHERE m.Id = (SELECT l.Id FROM ChatMessages l WHERE l.PeerId = m.PeerId ORDER BY l.SentAt DESC, l.Id DESC LIMIT 1)
            ORDER BY m.SentAt DESC;
            """);
        return [.. rows.Select(r => new ChatConversation(DeviceId.Parse(r.PeerId), r.ToMessage(), (int)r.Unread))];
    }

    /// <summary>The whole conversation, oldest first.</summary>
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(DeviceId peer, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MessageRow>(
            $"SELECT {Columns} FROM ChatMessages WHERE PeerId = @peer ORDER BY SentAt, Id;", new { peer = peer.Value });
        return [.. rows.Select(r => r.ToMessage())];
    }

    /// <summary>The conversation is being looked at: what the peer sent is read, and the peer is told ("Đã xem").</summary>
    public async Task MarkReadAsync(DeviceId peer, CancellationToken cancellationToken)
    {
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            string? newest = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT Id FROM ChatMessages WHERE PeerId = @peer AND Outgoing = 0 AND State = 'unread' ORDER BY Id DESC LIMIT 1;",
                new { peer = peer.Value });
            if (newest is null)
            {
                return;
            }

            await connection.ExecuteAsync(
                """
                UPDATE ChatMessages SET State = 'read' WHERE PeerId = @peer AND Outgoing = 0 AND State = 'unread';
                INSERT INTO ChatReceipts (PeerId, ReadUpTo) VALUES (@peer, @newest)
                  ON CONFLICT (PeerId) DO UPDATE SET ReadUpTo = MAX(ReadUpTo, excluded.ReadUpTo);
                """,
                new { peer = peer.Value, newest });
        }

        Changed?.Invoke(this, peer);
        Kick(peer);
    }

    // ───────────── Receiving (called by the API) ─────────────

    internal async Task<PeerResponse> HandleMessageAsync(DeviceId caller, ChatMessageBody body)
    {
        if (!TakeRate(caller))
        {
            return PeerResponse.Problem(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited);
        }

        string? text = body.Kind == ChatKinds.Text ? body.Text?.Trim() : null;
        bool voice = body.Kind == ChatKinds.Voice;
        if (!Guid.TryParseExact(body.Id, "N", out Guid id)
            || body.Kind is not { Length: > 0 and <= 32 }
            || (body.Kind == ChatKinds.Text && text is not { Length: > 0 and <= MaxTextLength })
            || (voice && !IsVoice(body.Audio, body.DurationMs ?? 0, body.Wave)))
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        long sentAt = Math.Min(body.SentAt.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());
        string? path = null;
        if (voice)
        {
            // Written before the row, so a stored voice message always has its file; a resent one writes the same bytes.
            path = VoicePath(id.ToString("N"));
            Directory.CreateDirectory(paths.ChatVoiceDirectory);
            await File.WriteAllBytesAsync(path, body.Audio!);
        }

        var message = new ChatMessage(
            caller, id.ToString("N"), false, body.Kind, text, FromMs(sentAt), ChatMessageState.Unread,
            voice ? body.DurationMs!.Value : 0, voice ? body.Wave : null, path);
        int added = await WriteAsync(
            """
            INSERT OR IGNORE INTO ChatMessages (PeerId, Id, Outgoing, Kind, Text, SentAt, ReceivedAt, State, DurationMs, Wave, VoicePath)
            VALUES (@peer, @Id, 0, @Kind, @Text, @sentAt, @now, 'unread', @DurationMs, @Wave, @VoicePath);
            """,
            new { peer = caller.Value, message.Id, message.Kind, message.Text, sentAt, now = now.ToUnixTimeMilliseconds(), message.DurationMs, message.Wave, message.VoicePath },
            CancellationToken.None);

        // Already here: the sender did not hear our answer and sent it again.
        if (added > 0)
        {
            usage.AddMessage(caller, sent: false);
            Changed?.Invoke(this, caller);
            Received?.Invoke(this, message);
        }

        return PeerResponse.Empty(HttpStatusCode.NoContent);
    }

    internal async Task<PeerResponse> HandleRecallAsync(DeviceId caller, ChatRecallBody body)
    {
        IReadOnlyList<string> offerIds = body.Offers ?? [];
        if ((body.Ids?.Count ?? 0) + offerIds.Count is not (> 0 and <= MaxRecall)
            || !(body.Ids ?? []).Concat(offerIds).All(id => Guid.TryParseExact(id, "N", out _)))
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        // Offers are the caller's own: the inbox only knows them under its id.
        await inbox.HandleRecalledAsync(caller, [.. offerIds.Select(id => Guid.ParseExact(id, "N").ToString("N"))]);
        string[] ids = [.. (body.Ids ?? []).Select(id => Guid.ParseExact(id, "N").ToString("N"))];
        List<string?> files;
        int changed;
        await using (SqliteConnection connection = await database.OpenAsync())
        {
            files = [.. await connection.QueryAsync<string?>(
                "SELECT VoicePath FROM ChatMessages WHERE PeerId = @peer AND Id IN @ids AND Outgoing = 0 AND VoicePath IS NOT NULL;", new { peer = caller.Value, ids })];
            // Read: nothing left to read, and its notification goes.
            changed = await connection.ExecuteAsync(
                """
                UPDATE ChatMessages SET Kind = 'recalled', Text = NULL, VoicePath = NULL, DurationMs = NULL, Wave = NULL, State = 'read'
                WHERE PeerId = @peer AND Id IN @ids AND Outgoing = 0 AND Kind <> 'recalled';
                """,
                new { peer = caller.Value, ids });
        }

        DeleteFiles(files);
        if (changed > 0)
        {
            Changed?.Invoke(this, caller);
        }

        return PeerResponse.Empty(HttpStatusCode.NoContent);
    }

    internal async Task<PeerResponse> HandleReceiptAsync(DeviceId caller, ChatReceiptBody body)
    {
        if (!Guid.TryParseExact(body.ReadUpTo, "N", out Guid upTo))
        {
            return PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        // A message read before its delivery was confirmed has arrived all the same.
        if (await WriteAsync(
                "UPDATE ChatMessages SET State = 'read' WHERE PeerId = @peer AND Outgoing = 1 AND Id <= @upTo AND State IN ('pending', 'delivered');",
                new { peer = caller.Value, upTo = upTo.ToString("N") },
                CancellationToken.None) > 0)
        {
            Changed?.Invoke(this, caller);
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
                SELECT PeerId FROM ChatMessages WHERE Outgoing = 1 AND State = 'pending' AND Kind <> 'recalled'
                UNION SELECT PeerId FROM ChatReceipts UNION SELECT PeerId FROM ChatRecalls;
                """);
        }

        HashSet<string> online = [.. directory.Snapshot().Where(p => p.State == PeerState.Online).Select(p => p.DeviceId.Value)];
        foreach (string peer in waiting.Where(online.Contains))
        {
            await SendPendingAsync(DeviceId.Parse(peer), cancellationToken);
        }
    }

    private void Kick(DeviceId peer) => _ = SendPendingAsync(peer, CancellationToken.None);

    /// <summary>One sender per peer keeps the order; a call while it runs makes it look once more when done.</summary>
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
                logger.LogWarning(ex, "Sending messages to {Peer} failed", peer);
            }
        }
    }

    private async Task SendOnceAsync(DeviceId peer, CancellationToken cancellationToken)
    {
        List<MessageRow> pending;
        string? receipt;
        List<(string Id, long IsOffer)> recalls;
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
        {
            // A recalled message that never left stays here.
            pending = [.. await connection.QueryAsync<MessageRow>(
                $"SELECT {Columns} FROM ChatMessages WHERE PeerId = @peer AND Outgoing = 1 AND State = 'pending' AND Kind <> 'recalled' ORDER BY SentAt, Id;",
                new { peer = peer.Value })];
            receipt = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT ReadUpTo FROM ChatReceipts WHERE PeerId = @peer;", new { peer = peer.Value });
            recalls = [.. await connection.QueryAsync<(string, long)>(
                "SELECT Id, IsOffer FROM ChatRecalls WHERE PeerId = @peer LIMIT @max;", new { peer = peer.Value, max = MaxRecall })];
        }

        foreach (MessageRow row in pending)
        {
            byte[]? audio = row.VoicePath is { } path && File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
            var body = new ChatMessageBody(row.Id, FromMs(row.SentAt), row.Kind, row.Text, (int?)row.DurationMs, row.Wave, audio);
            // The recording is gone (data folder cleaned): nothing to send.
            bool? delivered = row.Kind == ChatKinds.Voice && audio is null ? false : await PostAsync(peer, "chat/messages", body, cancellationToken);
            if (delivered is null)
            {
                return;
            }

            // A receipt may have come first and made it read.
            await WriteAsync(
                "UPDATE ChatMessages SET State = @state WHERE PeerId = @peer AND Id = @Id AND State = 'pending';",
                new { peer = peer.Value, row.Id, state = delivered.Value ? "delivered" : "failed" },
                cancellationToken);
            if (delivered.Value)
            {
                usage.AddMessage(peer, sent: true);
            }
            Changed?.Invoke(this, peer);
        }

        // Refused or not, a receipt is not worth keeping once the peer has answered.
        if (receipt is not null && await PostAsync(peer, "chat/receipts", new ChatReceiptBody(receipt), cancellationToken) is not null)
        {
            await WriteAsync(
                "DELETE FROM ChatReceipts WHERE PeerId = @peer AND ReadUpTo = @receipt;", new { peer = peer.Value, receipt }, cancellationToken);
        }

        // The same for recalls: a version without them cannot take anything back anyway.
        string[] recalledOffers = [.. recalls.Where(r => r.IsOffer != 0).Select(r => r.Id)];
        var recall = new ChatRecallBody([.. recalls.Where(r => r.IsOffer == 0).Select(r => r.Id)], recalledOffers.Length > 0 ? recalledOffers : null);
        if (recalls.Count > 0 && await PostAsync(peer, "chat/recalls", recall, cancellationToken) is not null)
        {
            string[] done = [.. recalls.Select(r => r.Id)];
            await WriteAsync("DELETE FROM ChatRecalls WHERE PeerId = @peer AND Id IN @done;", new { peer = peer.Value, done }, cancellationToken);
        }
    }

    /// <returns>True when taken, false when refused (no longer a contact, a version without chat), null to try later.</returns>
    private async Task<bool?> PostAsync<T>(DeviceId peer, string path, T body, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                peer, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, path)) { Content = JsonContent.Create(body) }, RequestTimeout, cancellationToken);
            await PeerClient.EnsureSuccessAsync(response, cancellationToken);
            return true;
        }
        catch (PeerUnreachableException)
        {
            logger.LogDebug("Messages to {Peer} wait: not reachable", peer);
            return null;
        }
        catch (PeerApiException ex) when (ex.Status is (int)HttpStatusCode.TooManyRequests or >= 500)
        {
            return null;
        }
        catch (PeerApiException ex)
        {
            logger.LogWarning("{Path} refused by {Peer}: {Code}", path, peer, ex.Code);
            return false;
        }
    }

    /// <summary>One more message from <paramref name="caller"/> this second, group messages included; false past the limit.</summary>
    internal bool TakeRate(DeviceId caller)
    {
        long second = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        lock (gate)
        {
            (long Second, int Count) rate = rates.TryGetValue(caller, out var r) && r.Second == second ? r : (second, 0);
            if (rate.Count >= MaxPerSecond)
            {
                return false;
            }

            rates[caller] = (second, rate.Count + 1);
            return true;
        }
    }

    /// <summary>An Ogg file of the right size, a length within the limit, a waveform of the right size.</summary>
    internal static bool IsVoice(byte[]? audio, int durationMs, byte[]? wave) =>
        audio is { Length: > 4 and <= MaxVoiceBytes } && audio.AsSpan().StartsWith("OggS"u8)
        && durationMs is > 0 and <= VoiceRecorder.MaxDurationMs + 1000
        && wave is { Length: <= VoiceRecorder.WaveBars };

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
        public string PeerId { get; init; } = "";

        public string Id { get; init; } = "";

        public long Outgoing { get; init; }

        public string Kind { get; init; } = "";

        public string? Text { get; init; }

        public long SentAt { get; init; }

        public string State { get; init; } = "";

        public long? DurationMs { get; init; }

        public byte[]? Wave { get; init; }

        public string? VoicePath { get; init; }

        public ChatMessage ToMessage() => new(
            DeviceId.Parse(PeerId), Id, Outgoing != 0, Kind, Text, FromMs(SentAt), Enum.Parse<ChatMessageState>(State, ignoreCase: true),
            (int)(DurationMs ?? 0), Wave, VoicePath);
    }

    private sealed class ConversationRow : MessageRow
    {
        public long Unread { get; init; }
    }
}
