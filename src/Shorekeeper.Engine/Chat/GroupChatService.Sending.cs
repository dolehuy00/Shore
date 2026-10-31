using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;

namespace Shorekeeper.Engine.Chat;

public sealed partial class GroupChatService
{
    // ───────────── Sending ─────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        directory.Changed += OnPeerChanged;
        groups.Changed += OnGroupsChanged;
        try
        {
            await MarkFromRemovedAsync();
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
        _ = MarkFromRemovedAsync();
    }

    /// <summary>
    /// What reached us from someone while they were out of the group and our member list did not know
    /// (docs/16-group-chat.md §3.6): kept, marked as such, and no longer passed on. Judged by when it reached us, which
    /// they cannot set:
    /// <list type="bullet">
    /// <item>within a time out the Host's signed list names (until they were let in again, if they were), read in our
    /// clock: the Host's, less how far it is ahead;</item>
    /// <item>for someone gone from our list with no such time (older than the Host keeps, or a Host without signed
    /// lists): after we last heard from the Host that they were a member.</item>
    /// </list>
    /// </summary>
    private async Task MarkFromRemovedAsync()
    {
        try
        {
            foreach (Group group in groups.GetAll())
            {
                GroupRosterRemoved[] removals = [.. groups.GetRoster(group.Id)?.Removed ?? []];
                TimeSpan hostAhead = groups.GetHostOffset(group.Id);
                List<(string Author, long From, long Until)> spans =
                [
                    .. removals.Select(r => (r.DeviceId, (r.At - hostAhead).ToUnixTimeMilliseconds(), r.BackAt is { } back ? (back - hostAhead).ToUnixTimeMilliseconds() : long.MaxValue)),
                    .. groups.GetDepartures(group.Id)
                        .Where(d => !removals.Any(r => r.DeviceId == d.Key.Value && r.BackAt is null))
                        .Select(d => (d.Key.Value, d.Value, long.MaxValue)),
                ];
                if (spans.Count == 0)
                {
                    continue;
                }

                int marked = 0;
                await using (SqliteConnection connection = await database.OpenAsync())
                {
                    foreach ((string author, long from, long until) in spans)
                    {
                        marked += await connection.ExecuteAsync(
                            """
                            UPDATE ChatGroupMessages SET OutOfGroup = 1
                            WHERE GroupId = @groupId AND AuthorId = @author AND Outgoing = 0 AND Deleted = 0 AND OutOfGroup = 0
                              AND ReceivedAt > @from AND ReceivedAt < @until;
                            """,
                            new { groupId = group.Id, author, from, until });
                    }
                }

                if (marked > 0)
                {
                    logger.LogWarning("Marked {Count} group messages of {Group} that came while their author was out of it", marked, group.Id);
                    Changed?.Invoke(this, group.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Marking group messages from removed members failed");
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
                UNION SELECT AuthorId FROM ChatGroupReceipts UNION SELECT PeerId FROM ChatGroupRecalls UNION SELECT PeerId FROM ChatGroupEdits
                UNION SELECT PeerId FROM ChatGroupReactionsOut;
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
        List<EditRow> edits;
        List<(string GroupId, string MessageId)> reactions;
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
            // The newest signed edit; none when the message is gone or taken back since.
            edits = [.. await connection.QueryAsync<EditRow>(
                """
                SELECT e.GroupId, e.Id, m.Text, m.EditedAt, m.EditSig FROM ChatGroupEdits e
                LEFT JOIN ChatGroupMessages m ON m.GroupId = e.GroupId AND m.Id = e.Id AND m.Kind = 'text' AND m.Deleted = 0
                WHERE e.PeerId = @peer;
                """,
                new { peer = peer.Value })];
            reactions = [.. await connection.QueryAsync<(string, string)>(
                "SELECT GroupId, MessageId FROM ChatGroupReactionsOut WHERE PeerId = @peer;", new { peer = peer.Value })];
        }

        // Heard directly without the capability: a version without group messages, which would only refuse them; or one
        // without replies and mentions, which could not check how a message that has them is signed.
        PeerInfo? heard = directory.Snapshot().FirstOrDefault(p => p.DeviceId == peer) is { Source: PeerSource.Direct, DiscoveryPort: > 0 } direct ? direct : null;
        bool old = heard is { CanGroupChat: false };
        bool before2 = heard is { CanChat2: false };
        foreach (DeliveryRow row in pending)
        {
            // Left, removed, or the group is gone (closed, we left, IT turned groups off): never for them now.
            if (!groups.IsMember(row.GroupId, peer))
            {
                await SetDeliveryAsync(row, peer, null, cancellationToken);
                continue;
            }

            if (old || (before2 && (row.ReplyTo is not null || row.Mentions is not null || row.EditSig is not null)))
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

            GroupChatMessageBody body = row.ToBody(audio);
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
                await EditedOnTheWayAsync(row, peer, cancellationToken);
            }
            else if (code == ApiErrorCodes.HostOffline)
            {
                // It has not heard from the Host lately; again later, in order, for as long as it takes.
                return;
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

        // The same for edits: a version without them answers like for a route it does not have. One edited again while
        // this one went stays, to go next.
        foreach (EditRow e in edits)
        {
            if (e.EditSig is null
                || (await PostAsync(peer, $"chat/groups/{e.GroupId}/edits", new GroupChatEditBody(e.Id, e.Text!, FromMs(e.EditedAt ?? 0), e.EditSig), cancellationToken))
                    .Taken is not null)
            {
                await WriteAsync(
                    """
                    DELETE FROM ChatGroupEdits WHERE GroupId = @GroupId AND PeerId = @peer AND Id = @Id
                      AND NOT EXISTS (SELECT 1 FROM ChatGroupMessages m WHERE m.GroupId = @GroupId AND m.Id = @Id AND m.EditedAt > @editedAt);
                    """,
                    new { e.GroupId, peer = peer.Value, e.Id, editedAt = e.EditedAt ?? 0 },
                    cancellationToken);
            }
        }

        // And reactions, the newest of each; none for whoever left, nor when the message is gone since.
        foreach ((string groupId, string messageId) in reactions)
        {
            (string? Emoji, long At)? mine;
            await using (SqliteConnection connection = await database.OpenAsync(cancellationToken))
            {
                mine = await ChatReactions.Groups.MineAsync(connection, groupId, messageId, engine.Identity.DeviceId);
            }

            if (mine is not { } reaction || !groups.IsMember(groupId, peer)
                || (await PostAsync(peer, $"chat/groups/{groupId}/reactions", new ChatReactionBody(messageId, reaction.Emoji, FromMs(reaction.At)), cancellationToken))
                    .Taken is not null)
            {
                await WriteAsync(
                    """
                    DELETE FROM ChatGroupReactionsOut WHERE GroupId = @groupId AND PeerId = @peer AND MessageId = @messageId
                      AND NOT EXISTS (SELECT 1 FROM ChatGroupReactions r WHERE r.GroupId = @groupId AND r.MessageId = @messageId AND r.ReactorId = @me AND r.At > @at);
                    """,
                    new { groupId, peer = peer.Value, messageId, me = engine.Identity.DeviceId.Value, at = mine?.At ?? 0 },
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Our message reached <paramref name="peer"/> with words since changed here: it is told like after any edit, and
    /// an edit made when the message had reached nobody becomes one.
    /// </summary>
    private async Task EditedOnTheWayAsync(DeliveryRow sent, DeviceId peer, CancellationToken cancellationToken)
    {
        if (sent.Outgoing == 0 || sent.Kind != ChatKinds.Text)
        {
            return;
        }

        await using SqliteConnection connection = await database.OpenAsync(cancellationToken);
        var found = (await connection.QueryAsync<MessageRow>(
            $"SELECT {Columns} FROM ChatGroupMessages WHERE GroupId = @GroupId AND Id = @Id AND Kind = 'text' AND Deleted = 0;", new { sent.GroupId, sent.Id })).ToList();
        if (found is not [var now] || (now.Text == sent.Text && now.EditedAt == sent.EditedAt))
        {
            return;
        }

        if (now.EditSig is null)
        {
            await SetWordsAsync(connection, now, now.Text!, edited: true);
        }

        await connection.ExecuteAsync(
            "INSERT OR IGNORE INTO ChatGroupEdits (GroupId, PeerId, Id) VALUES (@GroupId, @peer, @Id);", new { sent.GroupId, peer = peer.Value, sent.Id });
        Kick(peer);
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
}
