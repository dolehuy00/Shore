using System.Net;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;

namespace Shorekeeper.Engine.Chat;

public sealed partial class GroupChatService
{
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

        // "Chỉ nhắn khi Host online": our member list may be behind. Recalls and receipts still come: they only take away.
        if (!groups.CanChat(groupId))
        {
            return PeerResponse.Problem(HttpStatusCode.Forbidden, ApiErrorCodes.HostOffline);
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
            // Written before the row, so a stored voice message always has its file. A file of its own, never one
            // already here (the same id in another group or conversation): dropped below when not stored.
            path = ReceivedVoicePath(id);
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
            DeleteFiles([added < 2 ? path : null]);
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
}
