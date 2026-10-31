using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;

namespace Shorekeeper.Engine.Chat;

public sealed partial class GroupChatService
{
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
            WHERE GroupId = @groupId AND Seq > @after AND Deleted = 0 AND OutOfGroup = 0 AND SentAt >= @since
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
        // Past what was left out too (deleted, too old, out of the group), so the next pull starts after it.
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
                $"SELECT {Columns}, RecallSig FROM ChatGroupMessages WHERE GroupId = @groupId AND Id IN @ids AND Deleted = 0 AND OutOfGroup = 0 ORDER BY SentAt, Id;",
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
                row.Id, row.AuthorId, row.AuthorName, row.AuthorKey, FromMs(row.SentAt), row.Kind, row.Sig, row.Text, (int?)row.DurationMs, row.Wave, audio,
                row.ReplyTo, ChatReplies.Split(row.Mentions)));
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
                foreach (Group group in groups.GetAll().Where(g => groups.IsMember(g.Id, peer) && groups.CanChat(g.Id)))
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
}
