using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Engine.Tests;

/// <summary>How much a device that has been used for years holds (docs/22-scale.md §1).</summary>
internal sealed record ScaleSize
{
    public int Contacts { get; init; } = 30;

    /// <summary>The one conversation that went on for years.</summary>
    public int BigConversation { get; init; } = 50_000;

    public int OtherConversations { get; init; } = 2_000;

    /// <summary>Share of messages someone reacted to.</summary>
    public double Reacted { get; init; } = 0.2;

    public int BigGroupMembers { get; init; } = 100;

    public int BigGroupMessages { get; init; } = 30_000;

    /// <summary>Share of the big group's messages that are ours: each has a delivery row per other member.</summary>
    public double OursInGroup { get; init; } = 0.1;

    public int SmallGroups { get; init; } = 9;

    public int SmallGroupMessages { get; init; } = 1_000;

    public int ReceivedOffers { get; init; } = 2_000;

    public int SentOffers { get; init; } = 2_000;
}

/// <summary>What <see cref="ScaleData.Seed"/> made, to look things up by.</summary>
internal sealed record ScaleSeed(DeviceId BigPeer, IReadOnlyList<DeviceId> Contacts, string BigGroup, IReadOnlyList<string> Groups, string BigGroupOfferId);

/// <summary>
/// Fills a Shorekeeper database with a realistic amount of data, straight into its tables: for measuring at scale
/// (ScaleTests) and for trying the app on it (a folder given by SHOREKEEPER_DATA_DIR, docs/22-scale.md §3).
/// </summary>
internal static class ScaleData
{
    private static readonly string[] Emojis = ["👍", "❤️", "😂", "😮", "😢", "🙏"];
    private static readonly string[] Words =
    [
        "họp", "chiều", "nay", "file", "báo cáo", "gửi", "nhé", "ok", "đã nhận", "cảm ơn", "lúc", "giờ", "mai", "xem", "giúp",
        "mình", "bạn", "sửa", "lại", "bản", "mới", "ảnh", "chụp", "màn hình", "phòng", "kế toán", "số liệu", "tháng", "quý",
    ];

    public static DeviceId NewDevice() => DeviceId.FromSubjectPublicKeyInfo(RandomNumberGenerator.GetBytes(32));

    public static ScaleSeed Seed(string databaseFile, DeviceId me, ScaleSize size)
    {
        var random = new Random(20261102);
        using var connection = new SqliteConnection($"Data Source={databaseFile}");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = OFF;");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long twoYears = (long)TimeSpan.FromDays(730).TotalMilliseconds;

        // ── Contacts ──
        List<DeviceId> contacts = [.. Enumerable.Range(0, size.Contacts).Select(_ => NewDevice())];
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            SqliteCommand insert = Command(connection, tx,
                "INSERT INTO Peers (DeviceId, DisplayName, HostName, TrustLevel, TrustedAt, Os) VALUES ($id, $name, $host, 1, $at, $os);");
            for (int i = 0; i < contacts.Count; i++)
            {
                Run(insert, ("$id", contacts[i].Value), ("$name", $"Người thử {i + 1:00}"), ("$host", $"MAY-{i + 1:00}"), ("$at", now - twoYears),
                    ("$os", i % 3 == 0 ? "android" : "windows"));
            }

            tx.Commit();
        }

        // ── One-to-one conversations, with reactions ──
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            SqliteCommand message = Command(connection, tx,
                "INSERT INTO ChatMessages (PeerId, Id, Outgoing, Kind, Text, SentAt, ReceivedAt, State) VALUES ($peer, $id, $out, 'text', $text, $at, $at, 'read');");
            SqliteCommand reaction = Command(connection, tx,
                "INSERT INTO ChatReactions (PeerId, MessageId, ReactorId, Emoji, At) VALUES ($peer, $id, $who, $emoji, $at);");
            for (int c = 0; c < contacts.Count; c++)
            {
                int count = c == 0 ? size.BigConversation : size.OtherConversations;
                long start = now - (c == 0 ? twoYears : twoYears / 4) - c * 60_000L;
                long step = (now - start - 3_600_000) / count;
                for (int i = 0; i < count; i++)
                {
                    bool outgoing = random.Next(2) == 0;
                    string id = Guid.NewGuid().ToString("N");
                    long at = start + i * step;
                    Run(message, ("$peer", contacts[c].Value), ("$id", id), ("$out", outgoing ? 1 : 0), ("$text", Sentence(random)), ("$at", at));
                    if (random.NextDouble() < size.Reacted)
                    {
                        // The other one reacts to ours, we to theirs; sometimes both.
                        DeviceId who = outgoing ? contacts[c] : me;
                        Run(reaction, ("$peer", contacts[c].Value), ("$id", id), ("$who", who.Value), ("$emoji", Emojis[random.Next(Emojis.Length)]), ("$at", at + 60_000));
                        if (random.NextDouble() < 0.3)
                        {
                            DeviceId other = outgoing ? me : contacts[c];
                            Run(reaction, ("$peer", contacts[c].Value), ("$id", id), ("$who", other.Value), ("$emoji", Emojis[random.Next(Emojis.Length)]), ("$at", at + 120_000));
                        }
                    }
                }
            }

            tx.Commit();
        }

        // ── Groups: one big, the rest small ──
        List<string> groups = [];
        string bigGroup = "";
        for (int g = 0; g <= size.SmallGroups; g++)
        {
            bool big = g == 0;
            string groupId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            groups.Add(groupId);
            if (big)
            {
                bigGroup = groupId;
            }

            DeviceId host = NewDevice();
            List<DeviceId> members = [host, me, .. contacts.Take(big ? 20 : 5)];
            while (members.Count < (big ? size.BigGroupMembers : 8))
            {
                members.Add(NewDevice());
            }

            using SqliteTransaction tx = connection.BeginTransaction();
            Run(Command(connection, tx,
                    "INSERT INTO Groups (GroupId, Name, HostDeviceId, IsHostedByMe, CreatedAt, JoinedAt, State) VALUES ($g, $name, $host, 0, $at, $at, 'member');"),
                ("$g", groupId), ("$name", big ? "Cả công ty" : $"Nhóm thử {g}"), ("$host", host.Value), ("$at", now - twoYears));
            Run(Command(connection, tx, "INSERT INTO ChatGroups (GroupId, Name) VALUES ($g, $name);"), ("$g", groupId), ("$name", big ? "Cả công ty" : $"Nhóm thử {g}"));
            SqliteCommand member = Command(connection, tx,
                "INSERT INTO GroupMembers (GroupId, DeviceId, DisplayName, IsHost, JoinedAt) VALUES ($g, $id, $name, $host, $at);");
            for (int m = 0; m < members.Count; m++)
            {
                Run(member, ("$g", groupId), ("$id", members[m].Value), ("$name", m == 1 ? "Tôi" : $"Thành viên {m:000}"), ("$host", m == 0 ? 1 : 0), ("$at", now - twoYears));
            }

            SqliteCommand message = Command(connection, tx,
                """
                INSERT INTO ChatGroupMessages (GroupId, Id, AuthorId, AuthorName, AuthorKey, Outgoing, Kind, Text, SentAt, ReceivedAt, State, Sig, Seq)
                VALUES ($g, $id, $author, $name, $key, $out, 'text', $text, $at, $at, $state, $sig, $seq);
                """);
            SqliteCommand delivery = Command(connection, tx, "INSERT INTO ChatGroupDeliveries (GroupId, Id, PeerId, State) VALUES ($g, $id, $peer, 'read');");
            SqliteCommand reaction = Command(connection, tx,
                "INSERT INTO ChatGroupReactions (GroupId, MessageId, ReactorId, Emoji, At) VALUES ($g, $id, $who, $emoji, $at);");
            int count = big ? size.BigGroupMessages : size.SmallGroupMessages;
            long start = now - twoYears / 2 - g * 60_000L;
            long step = (now - start - 7_200_000) / count;
            byte[] key = new byte[91];
            byte[] sig = new byte[64];
            for (int i = 0; i < count; i++)
            {
                bool outgoing = random.NextDouble() < size.OursInGroup;
                DeviceId author = outgoing ? me : members[random.Next(2, members.Count)];
                string id = Guid.NewGuid().ToString("N");
                long at = start + i * step;
                Run(message, ("$g", groupId), ("$id", id), ("$author", author.Value), ("$name", outgoing ? "Tôi" : $"Thành viên {members.IndexOf(author):000}"),
                    ("$key", key), ("$out", outgoing ? 1 : 0), ("$text", Sentence(random)), ("$at", at), ("$state", outgoing ? "pending" : "read"), ("$sig", sig), ("$seq", i + 1));
                if (outgoing)
                {
                    foreach (DeviceId other in members.Where(m => m != me))
                    {
                        Run(delivery, ("$g", groupId), ("$id", id), ("$peer", other.Value));
                    }
                }

                // Groups react more: a few people on many messages.
                if (random.NextDouble() < size.Reacted * 1.5)
                {
                    int reactors = random.Next(1, big ? 8 : 4);
                    foreach (DeviceId who in members.OrderBy(_ => random.Next()).Take(reactors))
                    {
                        Run(reaction, ("$g", groupId), ("$id", id), ("$who", who.Value), ("$emoji", Emojis[random.Next(Emojis.Length)]), ("$at", at + 60_000));
                    }
                }
            }

            tx.Commit();
        }

        // ── Files: what came and what went in the last 90 days, a few files each ──
        long eightyDays = (long)TimeSpan.FromDays(80).TotalMilliseconds;
        string bigGroupOffer = "";
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            SqliteCommand offer = Command(connection, tx,
                """
                INSERT INTO InboxOffers (SenderId, OfferId, Note, TotalBytes, ReceivedAt, SentAt, ExpiresAt, State, GroupId)
                VALUES ($from, $id, NULL, $bytes, $at, $at, $expires, 'completed', $group);
                """);
            SqliteCommand file = Command(connection, tx,
                "INSERT INTO InboxFiles (SenderId, OfferId, FileId, RelativePath, Size, State) VALUES ($from, $id, $file, $path, $size, 'completed');");
            for (int i = 0; i < size.ReceivedOffers; i++)
            {
                DeviceId from = contacts[i % 4 == 0 ? 0 : random.Next(contacts.Count)];
                string id = Guid.NewGuid().ToString("N");
                long at = now - eightyDays + i * (eightyDays / size.ReceivedOffers);
                int files = random.Next(1, 4);
                Run(offer, ("$from", from.Value), ("$id", id), ("$bytes", files * 250_000L), ("$at", at), ("$expires", at + 86_400_000), ("$group", null));
                for (int f = 0; f < files; f++)
                {
                    Run(file, ("$from", from.Value), ("$id", id), ("$file", $"f{f}"), ("$path", f % 2 == 0 ? $"IMG_{i:0000}_{f}.jpg" : $"tai-lieu-{i}-{f}.pdf"), ("$size", 250_000L));
                }
            }

            SqliteCommand sent = Command(connection, tx,
                "INSERT INTO Offers (OfferId, GroupId, TotalBytes, State, CreatedAt, ExpiresAt, ClosedAt) VALUES ($id, $group, $bytes, $state, $at, $expires, $closed);");
            SqliteCommand sentFile = Command(connection, tx,
                "INSERT INTO OfferFiles (OfferId, FileId, RelativePath, Size, SourcePath) VALUES ($id, $file, $path, $size, $source);");
            SqliteCommand recipient = Command(connection, tx, "INSERT INTO OfferRecipients (OfferId, DeviceId, State) VALUES ($id, $to, 'completed');");
            for (int i = 0; i < size.SentOffers; i++)
            {
                string id = Guid.NewGuid().ToString("N");
                long at = now - eightyDays + i * (eightyDays / size.SentOffers);
                bool toGroup = i % 10 == 0;
                Run(sent, ("$id", id), ("$group", toGroup ? bigGroup : null), ("$bytes", 500_000L), ("$state", "done"), ("$at", at), ("$expires", at + 86_400_000), ("$closed", at + 3_600_000));
                Run(sentFile, ("$id", id), ("$file", "f0"), ("$path", $"gui-{i}.docx"), ("$size", 500_000L), ("$source", $@"C:\khong-co\gui-{i}.docx"));
                Run(recipient, ("$id", id), ("$to", (toGroup ? contacts[1] : contacts[i % 4 == 0 ? 0 : random.Next(contacts.Count)]).Value));
                if (toGroup)
                {
                    bigGroupOffer = id;
                }
            }

            tx.Commit();
        }

        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        return new ScaleSeed(contacts[0], contacts, bigGroup, groups, bigGroupOffer);
    }

    private static string Sentence(Random random) =>
        string.Join(' ', Enumerable.Range(0, random.Next(3, 14)).Select(_ => Words[random.Next(Words.Length)]));

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Run(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        command.Parameters.Clear();
        foreach ((string name, object? value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}
