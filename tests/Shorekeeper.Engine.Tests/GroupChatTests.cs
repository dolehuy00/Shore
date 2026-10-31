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
using Shorekeeper.Engine.Transfers;

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
        // Huy's member lists still on their way would note where Tuấn and Mai are again after a test forgets it.
        await host.Groups.WhenToldAsync();
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
        // Nor from Mai, who would pass them on.
        await OfflineAsync(tuan, mai);
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
        // At most its note, passed on by Mai: never the words.
        await Task.Delay(500, Ct);
        Assert.All(Messages(tuan), m => Assert.Equal((ChatKinds.Recalled, null), (m.Kind, m.Text)));
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
        // Nor from Mai, who would pass it on before she hears he is out.
        await OfflineAsync(tuan, mai);
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
        // Nor through Mai, who would pass it on.
        await ApartAsync(mai, tuan);

        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "một", Ct);

        await WaitUntil(() => Delivery(host, sent.Id, tuan) == GroupDeliveryState.Old && Delivery(host, sent.Id, mai) == GroupDeliveryState.Delivered);
        Assert.Empty(Messages(tuan));
    }

    [Fact]
    public async Task A_member_whose_list_is_behind_gets_it_once_it_catches_up()
    {
        var lanTime = new OffsetTime();
        await using TestNode lan = await TestNode.StartAsync("Lan", services: s => s.AddSingleton<TimeProvider>(lanTime));
        Group caughtUp = await BehindOnAsync(lan);

        GroupChatMessage sent = await lan.GroupChat.SendTextAsync(groupId, "Chào cả nhóm", Ct);

        await WaitUntil(() => RefusedAt(lan, sent.Id, tuan) is not null);
        Assert.Equal(GroupDeliveryState.Pending, Delivery(lan, sent.Id, tuan));
        Assert.Empty(Messages(tuan));

        // What the next answer from Huy brings; then Lan tries again, as on its 30-second round.
        await tuan.GroupStore.SaveAsync(caughtUp, Ct);
        Reappear(lan, tuan);

        await WaitUntil(() => Delivery(lan, sent.Id, tuan) == GroupDeliveryState.Delivered);
        Assert.Equal("Chào cả nhóm", Messages(tuan).Single().Text);
    }

    [Fact]
    public async Task A_member_refusing_for_two_minutes_fails_until_sent_again()
    {
        var lanTime = new OffsetTime();
        await using TestNode lan = await TestNode.StartAsync("Lan", services: s => s.AddSingleton<TimeProvider>(lanTime));
        Group caughtUp = await BehindOnAsync(lan);
        GroupChatMessage sent = await lan.GroupChat.SendTextAsync(groupId, "Chào cả nhóm", Ct);
        await WaitUntil(() => RefusedAt(lan, sent.Id, tuan) is not null);

        // Refused again once the two minutes are over.
        lanTime.Offset = GroupChatService.NotMemberGrace + TimeSpan.FromSeconds(1);
        Reappear(lan, tuan);
        await WaitUntil(() => Delivery(lan, sent.Id, tuan) == GroupDeliveryState.Failed);
        // Mai's copy goes its own way: it may still be on it.
        await WaitUntil(() => Delivery(lan, sent.Id, mai) == GroupDeliveryState.Delivered);

        // "Gửi lại" once the lists agree.
        await tuan.GroupStore.SaveAsync(caughtUp, Ct);
        await lan.GroupChat.RetryAsync(groupId, sent.Id, tuan.Id, Ct);

        await WaitUntil(() => Delivery(lan, sent.Id, tuan) == GroupDeliveryState.Delivered);
        Assert.Equal("Chào cả nhóm", Messages(tuan).Single().Text);
        Assert.Null(RefusedAt(lan, sent.Id, tuan));
    }

    [Fact]
    public async Task Nothing_of_a_removed_member_is_passed_on_even_dated_before_the_removal()
    {
        // Mai keeps the list from before, so she takes Tuấn's messages as one on his side would.
        await CutOffFromHostAsync(mai);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);
        foreach (GroupChatMessageBody body in (GroupChatMessageBody[])[Signed(tuan, "sau khi bị loại"), Signed(tuan, "lùi giờ", before - TimeSpan.FromMinutes(1))])
        {
            Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", body)).Status);
        }

        Assert.Equal(2, Messages(mai).Count);

        // Seeing Mai again, Huy pulls from her.
        Together(host, mai);
        await PulledAllAsync(host, mai);

        Assert.Empty(Messages(host));
    }

    [Fact]
    public async Task The_host_signs_the_member_list_with_who_left_and_members_take_only_that()
    {
        GroupRoster first = host.GroupStore.GetRoster(groupId)!;
        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);

        GroupRoster roster = host.GroupStore.GetRoster(groupId)!;
        Assert.True(roster.Version > first.Version);
        Assert.Equal(tuan.Id.Value, Assert.Single(roster.Removed).DeviceId);
        Assert.DoesNotContain(roster.Members, m => m.DeviceId == tuan.Id.Value);
        // The list is kept, then the members follow it.
        await WaitUntil(() => mai.GroupStore.GetRoster(groupId)?.Version == roster.Version && mai.GroupStore.Get(groupId) is { Members.Count: 2 });

        // Changed by whoever passes it on, or an older one: not taken.
        GroupRoster forged = roster with { Version = roster.Version + 1, Removed = [] };
        Assert.False(await mai.Groups.AdoptRosterAsync(groupId, forged));
        Assert.True(await mai.Groups.AdoptRosterAsync(groupId, first));
        Assert.Equal(roster.Version, mai.GroupStore.GetRoster(groupId)!.Version);
        Assert.Equal(2, mai.GroupStore.Get(groupId)!.Members.Count);
    }

    [Fact]
    public async Task A_removal_reaches_a_member_who_cannot_reach_the_host_through_another_member()
    {
        await using TestNode lan = await TestNode.StartAsync("Lan");
        await JoinAsync(lan);
        // Mai reaches neither Huy nor Tuấn: only Lan can tell her.
        await CutOffFromHostAsync(mai);
        Apart(mai, tuan);

        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);

        // Her next try at Huy fails, and she asks Lan instead (every 15 seconds).
        await WaitUntil(() => mai.GroupStore.Get(groupId) is { Members.Count: 3 } group && !group.Contains(tuan.Id), TimeSpan.FromSeconds(40));
        Together(mai, tuan);
        (HttpStatusCode status, string body) = await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", Signed(tuan, "vẫn ở đây?"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(ApiErrorCodes.NotMember, body);
    }

    [Fact]
    public async Task What_a_removed_member_got_in_while_the_list_was_behind_is_marked_once_it_is_known()
    {
        await CutOffFromHostAsync(mai);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", Signed(tuan, "trước khi bị loại"))).Status);
        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", Signed(tuan, "sau khi bị loại"))).Status);
        Assert.Equal(2, Messages(mai).Count);

        // However it reaches her: from Huy, or from another member.
        Assert.True(await mai.Groups.AdoptRosterAsync(groupId, host.GroupStore.GetRoster(groupId)!));

        await WaitUntil(() => Messages(mai).Any(m => m.OutOfGroup));
        Assert.Equal([("trước khi bị loại", false), ("sau khi bị loại", true)], Messages(mai).Select(m => (m.Text, m.OutOfGroup)));
    }

    [Fact]
    public async Task The_removal_time_is_read_in_the_members_own_clock()
    {
        // Huy's clock is two minutes ahead; Mai measures it on his answers.
        hostTime.Offset = TimeSpan.FromMinutes(2);
        await CutOffFromHostAsync(mai);
        Assert.InRange(mai.GroupStore.GetHostOffset(groupId), TimeSpan.FromSeconds(119), TimeSpan.FromSeconds(121));
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", Signed(tuan, "trước khi bị loại"))).Status);
        // Mai knows Huy's clock only to within a round trip, which a busy machine stretches: well apart, so the order
        // of the message and the removal is plain whatever the round trip was.
        await Task.Delay(2000, Ct);
        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", Signed(tuan, "sau khi bị loại"))).Status);

        Assert.True(await mai.Groups.AdoptRosterAsync(groupId, host.GroupStore.GetRoster(groupId)!));

        await WaitUntil(() => Messages(mai).Any(m => m.OutOfGroup));
        Assert.Equal([("trước khi bị loại", false), ("sau khi bị loại", true)], Messages(mai).Select(m => (m.Text, m.OutOfGroup)));
    }

    [Fact]
    public async Task The_host_tells_a_removal_at_once_to_a_member_who_cannot_ask_it()
    {
        // Mai no longer finds Huy, but he still finds her.
        Forget(mai, host);
        await host.Groups.RenameAsync(groupId, "Build QA 2");
        await WaitUntil(() => mai.Groups.IsHostReachable(groupId) == false);

        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);

        // Well before her next try, 15 seconds on.
        await WaitUntil(() => mai.GroupStore.Get(groupId) is { Members.Count: 2 }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_member_back_online_is_told_of_a_removal_it_missed()
    {
        await CutOffFromHostAsync(mai);
        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);
        await Task.Delay(300, Ct);
        Assert.Equal(3, mai.GroupStore.Get(groupId)!.Members.Count);

        // Huy finds her again; she still cannot find him.
        host.Sees(mai);

        await WaitUntil(() => mai.GroupStore.Get(groupId) is { Members.Count: 2 }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_host_quitting_right_after_a_removal_tells_it_first()
    {
        TestNode boss = await TestNode.StartAsync("Sếp");
        Together(boss, mai);
        Together(boss, tuan);
        Group group = await boss.Groups.CreateAsync("Ban giám đốc", Ct);
        foreach (TestNode member in (TestNode[])[mai, tuan])
        {
            await boss.Groups.InviteAsync(group.Id, member.Id, member.Id.ShortForm, "PC", Ct);
            await member.Groups.AcceptInvitationAsync(member.Groups.GetInvitations().Single(), Ct);
        }

        await WaitUntil(() => mai.GroupStore.Get(group.Id) is { Members.Count: 3 });
        // Mai only hears from the boss when he tells her.
        Forget(mai, boss);
        await boss.Groups.RenameAsync(group.Id, "Ban giám đốc 2");
        await WaitUntil(() => mai.Groups.IsHostReachable(group.Id) == false);

        await boss.Groups.RemoveMemberAsync(group.Id, tuan.Id);
        await boss.DisposeAsync();

        Assert.Equal(2, mai.GroupStore.Get(group.Id)!.Members.Count);
    }

    [Fact]
    public async Task What_came_while_someone_was_out_is_marked_and_not_passed_on_even_after_they_are_back()
    {
        await CutOffFromHostAsync(mai);
        await PostToMaiAsync("trước khi bị loại");
        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);
        await PostToMaiAsync("lúc bị loại");
        await WaitUntil(() => tuan.GroupStore.Get(groupId) is null);
        await host.Groups.InviteAsync(groupId, tuan.Id, tuan.Id.ShortForm, "PC", Ct);
        await tuan.Groups.AcceptInvitationAsync(tuan.Groups.GetInvitations().Single(), Ct);
        await WaitUntil(() => host.GroupStore.Get(groupId) is { Members.Count: 3 });
        await PostToMaiAsync("sau khi vào lại");

        // The newest list has Tuấn as a member again, and still names his time out.
        GroupRoster roster = host.GroupStore.GetRoster(groupId)!;
        Assert.NotNull(Assert.Single(roster.Removed).BackAt);
        Assert.True(await mai.Groups.AdoptRosterAsync(groupId, roster));

        await WaitUntil(() => Messages(mai).Any(m => m.OutOfGroup));
        Assert.Equal(
            [("trước khi bị loại", false), ("lúc bị loại", true), ("sau khi vào lại", false)],
            Messages(mai).Select(m => (m.Text, m.OutOfGroup)));
        // Tuấn is a member again, so his messages are passed on; not the one from his time out.
        Dictionary<string, string?> texts = Messages(mai).ToDictionary(m => m.Id, m => m.Text);
        Assert.Equal(
            ((string[])["trước khi bị loại", "sau khi vào lại"]).Order(),
            (await IndexAsync(tuan, mai)).Where(e => e.AuthorId == tuan.Id.Value).Select(e => texts.GetValueOrDefault(e.Id, e.Id)).Order());
    }

    [Fact]
    public async Task Someone_gone_without_a_time_has_what_came_after_the_host_last_said_they_were_in_marked()
    {
        await PostToMaiAsync("lúc còn trong nhóm");
        await CutOffFromHostAsync(mai);
        await PostToMaiAsync("không rõ lúc nào");
        await host.Groups.RemoveMemberAsync(groupId, tuan.Id);
        // A month on, the Host's list no longer names when Tuấn left: as for a device away that long.
        hostTime.Offset = GroupService.RemovedKept + TimeSpan.FromDays(1);
        await host.Groups.RenameAsync(groupId, "Build QA tháng sau");
        GroupRoster roster = host.GroupStore.GetRoster(groupId)!;
        Assert.Empty(roster.Removed);

        Assert.True(await mai.Groups.AdoptRosterAsync(groupId, roster));

        await WaitUntil(() => Messages(mai).Any(m => m.OutOfGroup));
        Assert.Equal([("lúc còn trong nhóm", false), ("không rõ lúc nào", true)], Messages(mai).Select(m => (m.Text, m.OutOfGroup)));
    }

    [Fact]
    public async Task A_group_messaging_only_while_the_host_is_online_waits_for_it()
    {
        await host.Groups.SetHostOnlyChatAsync(groupId, true);
        await WaitUntil(() => mai.GroupStore.Get(groupId) is { HostOnlyChat: true } && tuan.GroupStore.Get(groupId) is { HostOnlyChat: true });
        Assert.True(mai.GroupStore.CanChat(groupId));

        await CutOffFromHostAsync(mai);

        Assert.False(mai.GroupStore.CanChat(groupId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mai.GroupChat.SendTextAsync(groupId, "có ai không?", Ct));
        GroupChatMessage sent = await tuan.GroupChat.SendTextAsync(groupId, "họp lúc 3 giờ", Ct);
        await WaitUntil(() => Delivery(tuan, sent.Id, host) == GroupDeliveryState.Delivered);
        await Task.Delay(300, Ct);
        Assert.Equal(GroupDeliveryState.Pending, Delivery(tuan, sent.Id, mai));
        Assert.Empty(Messages(mai));

        // Huy answers her again (her next try, within 15 seconds); then Tuấn's next try gets through.
        Together(host, mai);
        await WaitUntil(() => mai.GroupStore.CanChat(groupId), TimeSpan.FromSeconds(40));
        Reappear(tuan, mai);
        await WaitUntil(() => Delivery(tuan, sent.Id, mai) == GroupDeliveryState.Delivered);
        Assert.Equal("họp lúc 3 giờ", Messages(mai).Single().Text);
    }

    [Fact]
    public async Task Search_finds_group_messages_but_not_deleted_ones()
    {
        GroupChatMessage holiday = await tuan.GroupChat.SendTextAsync(groupId, "Tuần sau nghỉ lễ 2 ngày", Ct);
        GroupChatMessage mine = await mai.GroupChat.SendTextAsync(groupId, "Nghỉ lễ đi đâu?", Ct);
        GroupChatMessage gone = await tuan.GroupChat.SendTextAsync(groupId, "nghỉ lễ nhầm", Ct);
        await WaitUntil(() => Messages(mai).Count == 3);
        await mai.GroupChat.DeleteAsync(groupId, [gone.Id], Ct);

        Assert.Equal([mine.Id, holiday.Id], (await mai.GroupChat.FindAsync("nghi le", 50, Ct)).Select(m => m.Id));
        Assert.Equal([holiday.Id], (await mai.GroupChat.FindAsync("2 ngay tuan", 50, Ct)).Select(m => m.Id));
    }

    [Fact]
    public async Task A_new_member_gets_what_the_members_said_but_not_who_left()
    {
        await mai.GroupChat.SendTextAsync(groupId, "Mai đây", Ct);
        await tuan.GroupChat.SendTextAsync(groupId, "Tuấn đây", Ct);
        await WaitUntil(() => Messages(host).Count == 2);
        await tuan.Groups.LeaveAsync(groupId);
        await WaitUntil(() => host.GroupStore.Get(groupId) is { Members.Count: 2 });

        await using TestNode lan = await TestNode.StartAsync("Lan");
        Together(lan, host);
        await host.Groups.InviteAsync(groupId, lan.Id, lan.Id.ShortForm, "PC", Ct);
        await lan.Groups.AcceptInvitationAsync(lan.Groups.GetInvitations().Single(), Ct);
        await WaitUntil(() => lan.GroupStore.Get(groupId) is { Members.Count: 3 });
        await PulledAllAsync(lan, host);

        Assert.Equal("Mai đây", Assert.Single(Messages(lan)).Text);
    }

    [Fact]
    public async Task A_member_passes_on_what_the_author_sent_while_another_was_away()
    {
        var received = new List<GroupChatMessage>();
        tuan.GroupChat.Received += (_, m) => received.Add(m);
        Offline(tuan);
        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "Chiều nay họp lúc 3 giờ", Ct);
        await WaitUntil(() => Messages(mai).Count == 1);

        // Tuấn sees Mai come online; Huy is still out of reach.
        tuan.Directory.Remove(mai.Id);
        tuan.Sees(mai);

        await WaitUntil(() => Messages(tuan).Count == 1);
        GroupChatMessage got = Messages(tuan).Single();
        Assert.Equal((sent.Id, host.Id, "Chiều nay họp lúc 3 giờ", ChatMessageState.Unread), (got.Id, got.Author, got.Text, got.State));
        Assert.Equal(sent.Id, Assert.Single(received).Id);
        // Huy only knows once it reaches Tuấn itself.
        Assert.Equal(GroupDeliveryState.Pending, Delivery(host, sent.Id, tuan));
        Online(tuan);
        await WaitUntil(() => Delivery(host, sent.Id, tuan) == GroupDeliveryState.Delivered);
        Assert.Single(Messages(tuan));
    }

    [Fact]
    public async Task A_message_changed_by_whoever_passes_it_on_is_dropped()
    {
        // Tuấn must not get it from Mai before she changes it.
        await OfflineAsync(tuan, mai);
        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "3 giờ", Ct);
        await WaitUntil(() => Messages(mai).Count == 1);
        using (SqliteConnection connection = await mai.Get<SqliteDatabase>().OpenAsync(Ct))
        {
            await connection.ExecuteAsync("UPDATE ChatGroupMessages SET Text = '4 giờ' WHERE Id = @Id;", new { sent.Id });
        }

        // Seeing Mai again, Tuấn pulls from her.
        Together(mai, tuan);
        await PulledAllAsync(tuan, mai);

        Assert.Empty(Messages(tuan));
        Online(tuan);
        await WaitUntil(() => Messages(tuan).Count == 1);
        Assert.Equal("3 giờ", Messages(tuan).Single().Text);
    }

    [Fact]
    public async Task Taking_back_reaches_a_member_who_got_it_from_someone_else()
    {
        Offline(tuan);
        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "nhầm nhóm", Ct);
        await WaitUntil(() => Messages(mai).Count == 1);
        await tuan.GroupChat.PullAllAsync(Ct);
        Assert.Equal("nhầm nhóm", Messages(tuan).Single().Text);

        await host.GroupChat.RecallAsync(groupId, [sent.Id], Ct);
        await WaitUntil(() => Messages(mai).Single().Kind == ChatKinds.Recalled);
        await tuan.GroupChat.PullAllAsync(Ct);

        GroupChatMessage got = Messages(tuan).Single();
        Assert.Equal((ChatKinds.Recalled, null, ChatMessageState.Read), (got.Kind, got.Text, got.State));
    }

    [Fact]
    public async Task A_message_taken_back_before_a_member_saw_it_never_shows_there()
    {
        Offline(tuan);
        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "nhầm nhóm", Ct);
        await WaitUntil(() => Messages(mai).Count == 1);
        await host.GroupChat.RecallAsync(groupId, [sent.Id], Ct);
        await WaitUntil(() => Messages(mai).Single().Kind == ChatKinds.Recalled);

        await tuan.GroupChat.PullAllAsync(Ct);

        GroupChatMessage got = Messages(tuan).Single();
        Assert.Equal((sent.Id, ChatKinds.Recalled, null, sent.SentAt), (got.Id, got.Kind, got.Text, got.SentAt));
    }

    [Fact]
    public async Task What_was_deleted_here_does_not_come_back()
    {
        GroupChatMessage sent = await host.GroupChat.SendTextAsync(groupId, "một", Ct);
        await WaitUntil(() => Messages(mai).Count == 1 && Messages(tuan).Count == 1);

        await mai.GroupChat.DeleteAsync(groupId, [sent.Id], Ct);
        await mai.GroupChat.PullAllAsync(Ct);

        Assert.Empty(Messages(mai));
        await mai.GroupChat.DeleteConversationAsync(groupId, Ct);
        await mai.GroupChat.PullAllAsync(Ct);
        Assert.Empty(Messages(mai));
    }

    [Fact]
    public async Task Pulling_again_asks_only_for_what_is_new()
    {
        Offline(tuan);
        await host.GroupChat.SendTextAsync(groupId, "một", Ct);
        await WaitUntil(() => Messages(mai).Count == 1);

        await tuan.GroupChat.PullAllAsync(Ct);

        long newest = Query<long>(mai, "SELECT MAX(Seq) FROM ChatGroupMessages WHERE GroupId = @groupId;", new { groupId });
        Assert.Equal(newest, Query<long>(tuan, "SELECT After FROM ChatGroupCursors WHERE GroupId = @groupId AND PeerId = @mai;", new { groupId, mai = mai.Id.Value }));
    }

    [Fact]
    public async Task A_new_member_gets_the_last_thirty_days_already_read()
    {
        Group other = await host.Groups.CreateAsync("Kế toán", Ct);
        await host.Groups.InviteAsync(other.Id, mai.Id, mai.Id.ShortForm, "PC", Ct);
        await mai.Groups.AcceptInvitationAsync(mai.Groups.GetInvitations().Single(), Ct);
        await WaitUntil(() => mai.GroupStore.Get(other.Id) is { Members.Count: 2 } && host.GroupStore.Get(other.Id) is { Members.Count: 2 });
        // Too old to pass on, and recent.
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(
            host, mai, $"chat/groups/{other.Id}/messages", Signed(host, "cũ", DateTimeOffset.UtcNow - TimeSpan.FromDays(31), other.Id))).Status);
        await host.GroupChat.SendTextAsync(other.Id, "mới", Ct);
        await WaitUntil(() => Messages(mai, other.Id).Count == 2);
        var received = new List<GroupChatMessage>();
        tuan.GroupChat.Received += (_, m) => received.Add(m);

        await host.Groups.InviteAsync(other.Id, tuan.Id, tuan.Id.ShortForm, "PC", Ct);
        await tuan.Groups.AcceptInvitationAsync(tuan.Groups.GetInvitations().Single(), Ct);

        await WaitUntil(() => Messages(tuan, other.Id).Count == 1);
        await tuan.GroupChat.PullAllAsync(Ct);
        GroupChatMessage got = Messages(tuan, other.Id).Single();
        Assert.Equal(("mới", ChatMessageState.Read), (got.Text, got.State));
        Assert.Empty(received);
        Assert.Equal(0, Assert.Single(await tuan.GroupChat.GetConversationsAsync(Ct)).Unread);
    }

    [Fact]
    public async Task A_group_can_be_muted_and_unmuted()
    {
        await mai.GroupChat.SetMutedAsync(groupId, true, Ct);
        Assert.Contains(groupId, await mai.GroupChat.GetMutedAsync(Ct));

        await mai.GroupChat.SetMutedAsync(groupId, false, Ct);
        Assert.Empty(await mai.GroupChat.GetMutedAsync(Ct));
        Assert.Equal(new[] { host.Id, tuan.Id }.OrderBy(d => d.Value), mai.GroupChat.OthersIn(groupId)!.Select(m => m.DeviceId).OrderBy(d => d.Value));
        Assert.Null(mai.GroupChat.OthersIn("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public async Task A_file_sent_to_the_group_is_kept_as_the_groups_and_can_be_taken_back()
    {
        string source = Path.Combine(host.WorkDirectory, "báo cáo.txt");
        await File.WriteAllTextAsync(source, "số liệu", Ct);
        Offline(tuan);
        string offerId = await host.Offers.CreateAsync(OfferService.Prepare([source]), [mai.Id, tuan.Id], null, snapshotLocked: false, groupId, Ct);
        await WaitUntil(() => mai.Inbox.GetOffers().Any(o => o.OfferId == offerId));
        Assert.Equal(groupId, mai.Inbox.GetOffers().Single(o => o.OfferId == offerId).GroupId);

        Assert.True(await host.GroupChat.RecallOfferAsync(groupId, offerId, Ct));

        await WaitUntil(() => mai.Inbox.GetOffers().Single(o => o.OfferId == offerId).Recalled);
        Assert.True(host.Offers.GetOffers().Single(o => o.OfferId == offerId).Recalled);
        Assert.False(await host.GroupChat.RecallOfferAsync(groupId, offerId, Ct));
        // Not an offer of another group, nor one sent to contacts.
        Assert.False(await host.GroupChat.RecallOfferAsync("0123456789abcdef0123456789abcdef", offerId, Ct));
    }

    [Fact]
    public async Task Members_announce_group_messages()
    {
        PresencePacket packet = mai.Get<ILocalPresence>().CreatePacket(PresencePacketTypes.Heartbeat);

        Assert.Contains(PresenceCapabilities.GroupChat, packet.Capabilities);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Huy and the member cannot reach each other: each is gone from the other's directory, and so are the addresses
    /// that once worked. Others still reach both.
    /// </summary>
    private void Offline(TestNode member) => Apart(host, member);

    /// <summary>
    /// <see cref="Offline"/>, and out of reach of <paramref name="relays"/> too (members who would pass messages on). A
    /// request still on its way from setting up can note an address again as it ends, so once those are done the
    /// addresses are forgotten once more: the member is really out of reach, however busy the machine.
    /// </summary>
    private async Task OfflineAsync(TestNode member, params TestNode[] relays)
    {
        for (int round = 0; round < 2; round++)
        {
            Offline(member);
            foreach (TestNode relay in relays)
            {
                Apart(relay, member);
            }

            if (round == 0)
            {
                await Task.Delay(500, Ct);
            }
        }
    }

    /// <summary><see cref="Apart"/>, made to last as <see cref="OfflineAsync"/> does.</summary>
    private static async Task ApartAsync(TestNode one, TestNode other)
    {
        Apart(one, other);
        await Task.Delay(500, Ct);
        Apart(one, other);
    }

    private void Online(TestNode member) => Together(host, member);

    private static void Apart(TestNode one, TestNode other)
    {
        Forget(one, other);
        Forget(other, one);
    }

    private static void Together(TestNode one, TestNode other)
    {
        one.Sees(other);
        other.Sees(one);
    }

    private static void Forget(TestNode node, TestNode other)
    {
        node.Directory.Remove(other.Id);
        using SqliteConnection connection = node.Get<SqliteDatabase>().OpenAsync(Ct).Result;
        connection.Execute("DELETE FROM PeerEndpoints WHERE DeviceId = @id;", new { id = other.Id.Value });
    }

    /// <summary>
    /// Lan joins, but Tuấn's list stays behind (<see cref="CutOffFromHostAsync"/>). Mai, who would pass Lan's message
    /// on, is out of his reach too. Returns his list as it would be once he catches up.
    /// </summary>
    private async Task<Group> BehindOnAsync(TestNode lan)
    {
        await JoinAsync(lan);

        await CutOffFromHostAsync(tuan);
        Apart(mai, tuan);

        Group caughtUp = tuan.GroupStore.Get(groupId)!;
        await tuan.GroupStore.SaveAsync(caughtUp with { Members = [.. caughtUp.Members.Where(m => m.DeviceId != lan.Id)] }, Ct);
        return caughtUp;
    }

    /// <summary>Tuấn, removed or not, posts straight to Mai, who takes it if her list still has him.</summary>
    private async Task PostToMaiAsync(string text) =>
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(tuan, mai, $"chat/groups/{groupId}/messages", Signed(tuan, text))).Status);

    /// <summary>Lan joins the group, seen by everyone and seeing everyone.</summary>
    private async Task JoinAsync(TestNode lan)
    {
        foreach (TestNode other in (TestNode[])[host, mai, tuan])
        {
            Together(lan, other);
        }

        await host.Groups.InviteAsync(groupId, lan.Id, lan.Id.ShortForm, "PC", Ct);
        await lan.Groups.AcceptInvitationAsync(lan.Groups.GetInvitations().Single(), Ct);
        await WaitUntil(() => ((TestNode[])[lan, mai, tuan]).All(n => n.GroupStore.Get(groupId) is { Members.Count: 4 }));
    }

    /// <summary>
    /// <paramref name="member"/> no longer reaches Huy, and the answer to the poll it had waiting (sped up by a rename)
    /// is the last one it gets: its member list stays as it is now.
    /// </summary>
    private async Task CutOffFromHostAsync(TestNode member)
    {
        Offline(member);
        await host.Groups.RenameAsync(groupId, host.GroupStore.Get(groupId)!.Name + "+");
        await WaitUntil(() => member.Groups.IsHostReachable(groupId) == false);
    }

    /// <summary>Waits until <paramref name="puller"/> has gone through all <paramref name="from"/> holds of the group.</summary>
    private async Task PulledAllAsync(TestNode puller, TestNode from)
    {
        long newest = Query<long>(from, "SELECT MAX(Seq) FROM ChatGroupMessages WHERE GroupId = @groupId;", new { groupId });
        await WaitUntil(() => Query<long?>(
            puller, "SELECT After FROM ChatGroupCursors WHERE GroupId = @groupId AND PeerId = @from;", new { groupId, from = from.Id.Value }) == newest);
    }

    /// <summary>When <paramref name="peer"/> first answered "not a member" to <paramref name="author"/>'s message.</summary>
    private long? RefusedAt(TestNode author, string id, TestNode peer) => Query<long?>(
        author, "SELECT RefusedAt FROM ChatGroupDeliveries WHERE GroupId = @groupId AND Id = @id AND PeerId = @peer;", new { groupId, id, peer = peer.Id.Value });

    /// <summary><paramref name="node"/> hears <paramref name="other"/> come online, which sends what waits for it.</summary>
    private static void Reappear(TestNode node, TestNode other)
    {
        node.Directory.Remove(other.Id);
        node.Sees(other);
    }

    private static T Query<T>(TestNode node, string sql, object parameters)
    {
        using SqliteConnection connection = node.Get<SqliteDatabase>().OpenAsync(Ct).Result;
        return connection.ExecuteScalar<T>(sql, parameters)!;
    }

    private List<GroupChatMessage> Messages(TestNode node) => Messages(node, groupId);

    private static List<GroupChatMessage> Messages(TestNode node, string group) => [.. node.GroupChat.GetMessagesAsync(group, Ct).Result];

    private GroupDeliveryState? Delivery(TestNode author, string id, TestNode peer) =>
        author.GroupChat.GetDeliveriesAsync(groupId, id, Ct).Result.SingleOrDefault(d => d.Peer == peer.Id)?.State;

    /// <summary>A text from <paramref name="author"/> to the group, signed as the app would, sent now or at <paramref name="at"/>.</summary>
    private GroupChatMessageBody Signed(TestNode author, string text, DateTimeOffset? at = null, string? group = null)
    {
        X509Certificate2 certificate = author.Get<ShorekeeperEngine>().Identity.Certificate;
        DateTimeOffset sentAt = DateTimeOffset.FromUnixTimeMilliseconds((at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        string id = Guid.CreateVersion7(sentAt).ToString("N");
        using ECDsa key = certificate.GetECDsaPrivateKey()!;
        byte[] sig = GroupChatSignature.SignMessage(
            key, new GroupChatContent(group ?? groupId, id, author.Id, sentAt.ToUnixTimeMilliseconds(), ChatKinds.Text, text, 0, null, null));
        return new GroupChatMessageBody(id, author.Id.Value, "peer", certificate.PublicKey.ExportSubjectPublicKeyInfo(), sentAt, ChatKinds.Text, sig, text);
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

    /// <summary>What <paramref name="to"/> offers <paramref name="from"/> to pull, from the start.</summary>
    private async Task<IReadOnlyList<GroupChatIndexEntry>> IndexAsync(TestNode from, TestNode to)
    {
        using HttpResponseMessage response = await from.Client.SendAsync(
            to.Id, u => new HttpRequestMessage(HttpMethod.Get, new Uri(u, $"chat/groups/{groupId}/index?after=0")), TimeSpan.FromSeconds(10), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GroupChatIndexResponse>(Ct))!.Entries;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync<T>(TestNode from, TestNode to, string path, T body)
    {
        using HttpResponseMessage response = await from.Client.SendAsync(
            to.Id, u => new HttpRequestMessage(HttpMethod.Post, new Uri(u, path)) { Content = JsonContent.Create(body) }, TimeSpan.FromSeconds(10), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            await Task.Delay(50, Ct);
        }
    }
}
