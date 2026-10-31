using System.Security.Cryptography;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Chat;
using Shorekeeper.UI.ViewModels;

namespace Shorekeeper.UI.Tests;

public class ChatTests
{
    private static readonly DeviceId Mai = NewDevice();
    private static readonly DeviceId Tuan = NewDevice();

    [Fact]
    public void Taking_back_is_cancelling_while_it_reached_nobody()
    {
        Assert.Equal("Hủy gửi", ChatViewModel.RecallLabel(reachedNobody: true));
        Assert.Equal("Thu hồi", ChatViewModel.RecallLabel(reachedNobody: false));

        Assert.Equal("Đã hủy gửi", ChatViewModel.RecalledText(pictures: true, cancelled: true));
        Assert.Equal("Tin nhắn đã được thu hồi", ChatViewModel.RecalledText(pictures: false, text: true));
        Assert.Equal("Ảnh đã được thu hồi", ChatViewModel.RecalledText(pictures: true));
        Assert.Equal("File đã được thu hồi", ChatViewModel.RecalledText(pictures: false));
    }

    [Fact]
    public void A_message_stands_for_itself_in_a_list_or_a_notification()
    {
        Assert.Equal("chào", ChatBubbleViewModel.TextOf(ChatKinds.Text, "chào", 0));
        Assert.Equal("Tin nhắn thoại 1:05", ChatBubbleViewModel.TextOf(ChatKinds.Voice, null, 65_000));
        Assert.Equal("Tin nhắn đã được thu hồi", ChatBubbleViewModel.TextOf(ChatKinds.Recalled, null, 0));
        Assert.Equal("Đã hủy gửi", ChatBubbleViewModel.TextOf(ChatKinds.Recalled, null, 0, cancelled: true));
        Assert.Equal("Tin nhắn này cần bản Shorekeeper mới hơn.", ChatBubbleViewModel.TextOf("sticker", "?", 0));
        Assert.Equal("0:09", ChatViewModel.Duration(9_999));
    }

    [Fact]
    public void Ours_waiting_for_everyone_can_be_cancelled_at_any_time()
    {
        ChatBubbleViewModel bubble = Bubble(Ours("họp nhé", ChatMessageState.Pending, DateTimeOffset.UtcNow.AddHours(-1)), first: true, reached: null);

        Assert.EndsWith("· Chờ gửi", bubble.StatusText);
        Assert.Equal("Hủy gửi", bubble.RecallText);
        Assert.True(bubble.CanRecall);
        Assert.False(bubble.ShowsAuthor);
    }

    [Fact]
    public void Ours_that_reached_members_counts_who_has_it_then_who_read_it()
    {
        GroupChatMessage message = Ours("họp nhé", ChatMessageState.Delivered, DateTimeOffset.UtcNow);

        Assert.EndsWith("· Đã nhận 1/2", Bubble(message, first: true, [new(Mai, GroupDeliveryState.Delivered), new(Tuan, GroupDeliveryState.Pending)]).StatusText);
        Assert.EndsWith("· Đã xem 1/2", Bubble(message, first: true, [new(Mai, GroupDeliveryState.Read), new(Tuan, GroupDeliveryState.Pending)]).StatusText);
        ChatBubbleViewModel bubble = Bubble(message, first: true, [new(Mai, GroupDeliveryState.Read), new(Tuan, GroupDeliveryState.Read)]);
        Assert.EndsWith("· Đã xem 2/2", bubble.StatusText);
        Assert.Equal("Thu hồi", bubble.RecallText);
        Assert.True(bubble.CanRecall);
    }

    [Fact]
    public void Ours_is_taken_back_only_within_five_minutes_once_it_reached_someone()
    {
        ChatBubbleViewModel bubble = Bubble(Ours("cũ", ChatMessageState.Delivered, DateTimeOffset.UtcNow.AddMinutes(-6)), first: true, reached: null);

        Assert.False(bubble.CanRecall);
        Assert.Equal("Thu hồi", bubble.RecallText);
    }

    [Fact]
    public void Ours_that_reached_nobody_ever_says_so_with_a_way_to_try_again()
    {
        ChatBubbleViewModel bubble = Bubble(Ours("không tới", ChatMessageState.Failed, DateTimeOffset.UtcNow), first: true, reached: null);

        Assert.True(bubble.IsFailed);
        Assert.EndsWith("· Không gửi được", bubble.StatusText);
    }

    [Fact]
    public void Ours_cancelled_before_it_left_reads_as_cancelled()
    {
        GroupChatMessage message = Ours(null, ChatMessageState.Pending, DateTimeOffset.UtcNow) with { Kind = ChatKinds.Recalled };

        ChatBubbleViewModel bubble = Bubble(message, first: true, reached: null);

        Assert.Equal("Đã hủy gửi", bubble.Text);
        Assert.False(bubble.CanRecall);
        Assert.False(bubble.CanCopy);
    }

    [Fact]
    public void A_members_message_shows_who_wrote_it_first_in_a_run_with_its_links()
    {
        GroupChatMessage message = new("g", "m1", Mai, "Mai", Outgoing: false, ChatKinds.Text, "xem https://example.com/a nhé", DateTimeOffset.UtcNow, ChatMessageState.Read);

        ChatBubbleViewModel first = Bubble(message, first: true, reached: null, author: "Mai");
        ChatBubbleViewModel next = Bubble(message, first: false, reached: null, author: "Mai");

        Assert.True(first.ShowsAuthor);
        Assert.Equal("M", first.AuthorInitial);
        Assert.False(next.ShowsAuthor);
        Assert.Equal(["https://example.com/a"], first.Links);
        Assert.False(first.CanRecall);
        Assert.DoesNotContain("·", first.StatusText);
        Assert.Equal(ChatBubbleViewModel.BrushOf(Mai), first.AuthorBrush);
        Assert.False(first.IsOutOfGroup);
    }

    [Fact]
    public void A_message_that_came_while_its_author_was_out_of_the_group_is_marked()
    {
        GroupChatMessage message = new(
            "g", "m2", Tuan, "Tuấn", Outgoing: false, ChatKinds.Text, "vẫn ở đây", DateTimeOffset.UtcNow, ChatMessageState.Read, OutOfGroup: true);

        Assert.True(Bubble(message, first: true, reached: null, author: "Tuấn").IsOutOfGroup);
    }

    [Fact]
    public void A_voice_message_has_its_waveform_and_length()
    {
        GroupChatMessage message = new(
            "g", "v1", Mai, "Mai", Outgoing: false, ChatKinds.Voice, null, DateTimeOffset.UtcNow, ChatMessageState.Read,
            DurationMs: 12_000, Wave: [0, 255]);

        ChatBubbleViewModel bubble = Bubble(message, first: true, reached: null, author: "Mai");

        Assert.True(bubble.IsVoice);
        Assert.False(bubble.CanCopy);
        Assert.Equal("0:12", bubble.DurationText);
        Assert.Equal([3.0, 24.0, 3.0], bubble.Bars.Take(3));
    }

    [Fact]
    public void A_one_to_one_message_says_how_far_ours_got_only_on_the_newest()
    {
        ChatMessage read = new(Mai, "c1", Outgoing: true, ChatKinds.Text, "đã xem chưa", DateTimeOffset.UtcNow, ChatMessageState.Read);

        Assert.EndsWith("· Đã xem", new ChatBubbleViewModel("c1", null!).Update(read, lastMine: true).StatusText);
        Assert.DoesNotContain("·", new ChatBubbleViewModel("c1", null!).Update(read, lastMine: false).StatusText);
        Assert.EndsWith("· Chờ gửi", new ChatBubbleViewModel("c1", null!).Update(read with { State = ChatMessageState.Pending }, lastMine: false).StatusText);
    }

    [Fact]
    public void A_reply_quotes_what_it_answers_or_says_it_is_gone()
    {
        GroupChatMessage question = new("g", "q1", Mai, "Mai", Outgoing: false, ChatKinds.Text, "Mấy giờ họp?", DateTimeOffset.UtcNow, ChatMessageState.Read);
        GroupChatMessage voice = question with { Id = "v1", Kind = ChatKinds.Voice, Text = null, DurationMs = 12_000 };

        ChatBubbleViewModel answer = Bubble(Ours("3 giờ", ChatMessageState.Delivered, DateTimeOffset.UtcNow) with { ReplyTo = "q1" }, first: true, reached: null);

        Assert.Equal("q1", answer.ReplyTo);
        Assert.Equal("Mấy giờ họp?", ChatViewModel.QuoteText(Bubble(question, first: true, reached: null, author: "Mai")));
        Assert.Equal("Tin nhắn thoại 0:12", ChatViewModel.QuoteText(Bubble(voice, first: true, reached: null, author: "Mai")));
        Assert.Equal("Tin nhắn không còn", ChatViewModel.QuoteText(null));
    }

    [Fact]
    public void Ours_in_words_is_edited_within_fifteen_minutes_or_while_it_reached_nobody()
    {
        Assert.True(Bubble(Ours("mới", ChatMessageState.Delivered, DateTimeOffset.UtcNow.AddMinutes(-14)), first: true, reached: null).CanEdit);
        Assert.False(Bubble(Ours("cũ", ChatMessageState.Delivered, DateTimeOffset.UtcNow.AddMinutes(-16)), first: true, reached: null).CanEdit);
        Assert.True(Bubble(Ours("chờ", ChatMessageState.Pending, DateTimeOffset.UtcNow.AddHours(-1)), first: true, reached: null).CanEdit);
        GroupChatMessage voice = Ours(null, ChatMessageState.Delivered, DateTimeOffset.UtcNow) with { Kind = ChatKinds.Voice, DurationMs = 1_000 };
        Assert.False(Bubble(voice, first: true, reached: null).CanEdit);
        GroupChatMessage theirs = new("g", "m3", Mai, "Mai", Outgoing: false, ChatKinds.Text, "của Mai", DateTimeOffset.UtcNow, ChatMessageState.Read);
        Assert.False(Bubble(theirs, first: true, reached: null, author: "Mai").CanEdit);
    }

    [Fact]
    public void An_edited_message_says_so_by_its_time_with_its_new_links()
    {
        GroupChatMessage message = new(
            "g", "m4", Mai, "Mai", Outgoing: false, ChatKinds.Text, "xem https://example.com/b", DateTimeOffset.UtcNow, ChatMessageState.Read);
        ChatBubbleViewModel bubble = Bubble(message, first: true, reached: null, author: "Mai");
        Assert.DoesNotContain("Đã sửa", bubble.StatusText);

        bubble.Update(message with { Text = "xem https://example.com/c", EditedAt = DateTimeOffset.UtcNow }, "Mai", first: true, reached: null);

        Assert.EndsWith("· Đã sửa", bubble.StatusText);
        Assert.Equal(["https://example.com/c"], bubble.Links);
        ChatMessage oneToOne = new(Mai, "c2", Outgoing: true, ChatKinds.Text, "đã sửa", DateTimeOffset.UtcNow, ChatMessageState.Read, EditedAt: DateTimeOffset.UtcNow);
        Assert.EndsWith("· Đã sửa · Đã xem", new ChatBubbleViewModel("c2", null!).Update(oneToOne, lastMine: true).StatusText);
    }

    [Fact]
    public void Each_member_keeps_one_color()
    {
        Assert.Same(ChatBubbleViewModel.BrushOf(Mai), ChatBubbleViewModel.BrushOf(Mai));
    }

    private static GroupChatMessage Ours(string? text, ChatMessageState state, DateTimeOffset sentAt) =>
        new("g", Guid.NewGuid().ToString("N"), Tuan, "Tuấn", Outgoing: true, ChatKinds.Text, text, sentAt, state);

    /// <summary>A bubble on its own: <see cref="ChatBubbleViewModel.Update(GroupChatMessage, string, bool, IReadOnlyList{GroupDelivery}?)"/> needs no conversation.</summary>
    private static ChatBubbleViewModel Bubble(GroupChatMessage message, bool first, IReadOnlyList<GroupDelivery>? reached, string author = "Bạn") =>
        new ChatBubbleViewModel(message.Id, null!).Update(message, author, first, reached);

    private static DeviceId NewDevice()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return DeviceId.FromSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo());
    }
}
