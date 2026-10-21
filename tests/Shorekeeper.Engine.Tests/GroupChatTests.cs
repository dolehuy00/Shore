using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Storage;

namespace Shorekeeper.Engine.Tests;

/// <summary>Group messages between real engines over HTTPS (docs/16-group-chat.md). Nobody here is a contact of anybody.</summary>
public sealed class GroupChatTests : IAsyncLifetime
{
    private readonly OffsetTime hostTime = new();
    private TestNode host = null!;
    private TestNode mai = null!;
    private TestNode tuan = null!;
    private string groupId = null!;

    public async ValueTask InitializeAsync()
    {
        host = await TestNode.StartAsync("Huy", services: s => s.AddSingleton<TimeProvider>(hostTime));
        mai = await TestNode.StartAsync("Mai");
        tuan = await TestNode.StartAsync("Tuấn");
        // What discovery would do: everyone knows where everyone listens.
        foreach (TestNode node in (TestNode[])[host, mai, tuan])
        {
            foreach (TestNode other in (TestNode[])[host, mai, tuan])
            {
                if (node != other)
                {
                    node.Sees(other);
                }
            }
        }

        Group group = await host.Groups.CreateAsync("Build QA", Ct);
        foreach (TestNode member in (TestNode[])[mai, tuan])
        {
            await host.Groups.InviteAsync(group.Id, member.Id, member.Id.ShortForm, "PC", Ct);
            await member.Groups.AcceptInvitationAsync(member.Groups.GetInvitations().Single(), Ct);
        }

        await WaitUntil(() => mai.GroupStore.Get(group.Id) is { Members.Count: 3 } && tuan.GroupStore.Get(group.Id) is { Members.Count: 3 });
        groupId = group.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await mai.DisposeAsync();
        await tuan.DisposeAsync();
    }

    [Fact]
    public async Task A_message_reaches_every_member_and_the_author_sees_each()
    {
        var received = new List<GroupChatMessage>();
        mai.GroupChat.Received += (_, m) => received.Add(m);

        GroupChatMessage sent = await mai.GroupChat.SendTextAsync(groupId, "  Chiều nay họp lúc 3 giờ nhé ", Ct);

        foreach (TestNode member in (TestNode[])[host, tuan])
        {
            await WaitUntil(() => Messages(member).Count == 1);
            GroupChatMessage got = Messages(member).Single();
            Assert.Equal(
                (sent.Id, mai.Id, "Mai", "Chiều nay họp lúc 3 giờ nhé", false, ChatMessageState.Unread, sent.SentAt),
                (got.Id, got.Author, got.AuthorName, got.Text, got.Outgoing, got.State, got.SentAt));
            GroupChatConversation conversation = Assert.Single(await member.GroupChat.GetConversationsAsync(Ct));
            Assert.Equal((groupId, "Build QA", 1), (conversation.GroupId, conversation.Name, conversation.Unread));
        }

        await WaitUntil(() => Delivery(mai, sent.Id, host) == GroupDeliveryState.Delivered && Delivery(mai, sent.Id, tuan) == GroupDeliveryState.Delivered);
        Assert.Equal(ChatMessageState.Delivered, Messages(mai).Single().State);
        Assert.Empty(received);
    }

    [Fact]
    public async Task A_voice_message_arrives_with_its_recording()
    {
        VoiceNote note = Voice(seconds: 1);

        GroupChatMessage sent = await host.GroupChat.SendVoiceAsync(groupId, note, Ct);

        await WaitUntil(() => Messages(tuan).Count == 1);
        GroupChatMessage got = Messages(tuan).Single();
        Assert.Equal((sent.Id, ChatKinds.Voice, note.DurationMs), (got.Id, got.Kind, got.DurationMs));
        Assert.Equal(note.Ogg, await File.ReadAllBytesAsync(got.VoicePath!, Ct));
    }

    [Fact]
    public async Task Messages_wait_for_an_offline_member_and_arrive_in_order()
    {
        Offline(tuan);
        string[] texts = ["một", "hai", "ba"];
        foreach (string text in texts)
        {
            await host.GroupChat.SendTextAsync(groupId, text, Ct);
        }

        await WaitUntil(() => Messages(mai).Count == 3);
        await Task.Delay(300, Ct);
        Assert.Empty(Messages(tuan));
        Assert.All(Messages(host), m => Assert.Equal(GroupDeliveryState.Pending, Delivery(host, m.Id, tuan)));

        Online(tuan);

        await WaitUntil(() => Messages(tuan).Count == 3);
        Assert.Equal(texts, Messages(tuan).Select(m => m.Text));
        await WaitUntil(() => Messages(host).All(m => Delivery(host, m.Id, tuan) == GroupDeliveryState.Delivered));
    }

    [Fact]
    public async Task Reading_tells_the_author_who_read_it()
    {
        GroupChatMessage first = await host.GroupChat.SendTextAsync(groupId, "một", Ct);
        GroupChatMessage second = await host.GroupChat.SendTextAsync(groupId, "hai", Ct);
        await WaitUntil(() => Messages(mai).Count == 2 && Messages(tuan).Count == 2);
        await WaitUntil(() => Delivery(host, second.Id, tuan) == GroupDeliveryState.Delivered);

        await mai.GroupChat.MarkReadAsync(groupId, Ct);

        Assert.Equal(0, Assert.Single(await mai.GroupChat.GetConversationsAsync(Ct)).Unread);
        await WaitUntil(() => Delivery(host, first.Id, mai) == GroupDeliveryState.Read && Delivery(host, second.Id, mai) == GroupDeliveryState.Read);
        Assert.Equal(GroupDeliveryState.Delivered, Delivery(host, second.Id, tuan));
        Assert.Equal(ChatMessageState.Read, Messages(host).Single(m => m.Id == second.Id).State);
    }

    [Fact]
    public async Task A_reply_stays_below_the_message_it_answers_when_the_clocks_differ()
    {
        hostTime.Offset = TimeSpan.FromMinutes(2);
        GroupChatMessage question = await host.GroupChat.SendTextAsync(groupId, "Mấy giờ họp?", Ct);
        await WaitUntil(() => Messages(mai).Count == 1);

        GroupChatMessage answer = await mai.GroupChat.SendTextAsync(groupId, "3 giờ", Ct);

        Assert.True(answer.SentAt > question.SentAt);
        await WaitUntil(() => Messages(tuan).Count == 2);
        Assert.Equal(["Mấy giờ họp?", "3 giờ"], Messages(tuan).Select(m => m.Text));
    }

    [Fact]
    public async Task Taking_a_message_back_reaches_who_has_it_and_stops_the_rest()
    {
        Offline(tuan);
        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "nhầm nhóm", Ct);
        await WaitUntil(() => Messages(mai).Count == 1);

        await host.GroupChat.RecallAsync(groupId, [sent.Id], Ct);

        Assert.Equal(ChatKinds.Recalled, Messages(host).Single().Kind);
        await WaitUntil(() => Messages(mai).Single().Kind == ChatKinds.Recalled);
        Assert.Null(Messages(mai).Single().Text);
        Assert.Equal(ChatMessageState.Read, Messages(mai).Single().State);
        Online(tuan);
        await Task.Delay(500, Ct);
        Assert.Empty(Messages(tuan));
    }

    [Fact]
    public async Task Someone_outside_the_group_is_refused_even_as_a_contact()
    {
        await using TestNode lan = await TestNode.StartAsync("Lan");
        await lan.ConnectAsync(mai);

        (HttpStatusCode status, string body) = await PostAsync(lan, mai, $"chat/groups/{groupId}/messages", Signed(lan, "chào"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(ApiErrorCodes.NotMember, body);
        Assert.Empty(Messages(mai));
    }

    [Fact]
    public async Task A_message_changed_after_signing_or_signed_for_someone_else_is_refused()
    {
        GroupChatMessageBody changed = Signed(tuan, "3 giờ") with { Text = "4 giờ" };
        GroupChatMessageBody notTheirs = Signed(tuan, "3 giờ") with { AuthorId = host.Id.Value };
        GroupChatMessageBody otherKey = Signed(tuan, "3 giờ") with { AuthorKey = host.Get<ShorekeeperEngine>().Identity.Certificate.PublicKey.ExportSubjectPublicKeyInfo() };

        foreach (GroupChatMessageBody body in (GroupChatMessageBody[])[changed, notTheirs, otherKey])
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", body)).Status);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", Signed(tuan, "3 giờ"))).Status);
        Assert.Equal("3 giờ", Messages(mai).Single().Text);
    }

    [Fact]
    public async Task A_removed_member_gets_nothing_more_and_is_refused()
    {
        Offline(tuan);
        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "một", Ct);
        Assert.Equal(GroupDeliveryState.Pending, Delivery(host, sent.Id, tuan));

        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);
        Online(tuan);

        await WaitUntil(() => host.GroupChat.GetDeliveriesAsync(groupId, sent.Id, Ct).Result.All(d => d.Peer != tuan.Id));
        Assert.Empty(Messages(tuan));
        (HttpStatusCode status, string body) = await PostAsync(tuan, host, $"chat/groups/{groupId}/messages", Signed(tuan, "vẫn ở đây?"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(ApiErrorCodes.NotMember, body);
    }

    [Fact]
    public async Task A_version_without_group_messages_is_not_sent_to()
    {
        // Heard directly, announcing no "chat-group".
        host.Directory.Observe(
            new PresencePacket { Type = PresencePacketTypes.Heartbeat, Id = tuan.Id.Value, Name = "peer", Port = tuan.ApiPort, Capabilities = [PresenceCapabilities.Chat] },
            IPAddress.Loopback,
            discoveryPort: 47470);

        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "một", Ct);

        await WaitUntil(() => Delivery(host, sent.Id, tuan) == GroupDeliveryState.Old && Delivery(host, sent.Id, mai) == GroupDeliveryState.Delivered);
        Assert.Empty(Messages(tuan));
    }

    [Fact]
    public async Task Members_announce_group_messages()
    {
        PresencePacket packet = mai.Get<ILocalPresence>().CreatePacket(PresencePacketTypes.Heartbeat);

        Assert.Contains(PresenceCapabilities.GroupChat, packet.Capabilities);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Huy cannot reach the member: it is gone from the directory, and so are the addresses that once worked.</summary>
    private void Offline(TestNode member)
    {
        host.Directory.Remove(member.Id);
        using SqliteConnection connection = host.Get<SqliteDatabase>().OpenAsync(Ct).Result;
        connection.Execute("DELETE FROM PeerEndpoints WHERE DeviceId = @id;", new { id = member.Id.Value });
    }

    private void Online(TestNode member) => host.Sees(member);

    private List<GroupChatMessage> Messages(TestNode node) => [.. node.GroupChat.GetMessagesAsync(groupId, Ct).Result];

    private GroupDeliveryState? Delivery(TestNode author, string id, TestNode peer) =>
        author.GroupChat.GetDeliveriesAsync(groupId, id, Ct).Result.SingleOrDefault(d => d.Peer == peer.Id)?.State;

    /// <summary>A text from <paramref name="author"/> to the group, signed as the app would.</summary>
    private GroupChatMessageBody Signed(TestNode author, string text)
    {
        X509Certificate2 certificate = author.Get<ShorekeeperEngine>().Identity.Certificate;
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        string id = Guid.CreateVersion7(now).ToString("N");
        using ECDsa key = certificate.GetECDsaPrivateKey()!;
        byte[] sig = GroupChatSignature.SignMessage(key, new GroupChatContent(groupId, id, author.Id, now.ToUnixTimeMilliseconds(), ChatKinds.Text, text, 0, null, null));
        return new GroupChatMessageBody(id, author.Id.Value, "peer", certificate.PublicKey.ExportSubjectPublicKeyInfo(), now, ChatKinds.Text, sig, text);
    }

    /// <summary>A tone recorded as the microphone would deliver it.</summary>
    private static VoiceNote Voice(int seconds)
    {
        var recorder = new VoiceRecorder();
        float[] frame = new float[AudioStreamFormat.FrameValues];
        for (int f = 0; f < seconds * 100; f++)
        {
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = 0.3f * MathF.Sin((f * frame.Length + i) * 0.05f);
            }

            recorder.Add(frame);
        }

        return recorder.Finish();
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync<T>(TestNode from, TestNode to, string path, T body)
    {
        using HttpResponseMessage response = await from.Client.SendAsync(
            to.Id, u => new HttpRequestMessage(HttpMethod.Post, new Uri(u, path)) { Content = JsonContent.Create(body) }, TimeSpan.FromSeconds(10), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 15s.");
            await Task.Delay(50, Ct);
        }
    }
}
