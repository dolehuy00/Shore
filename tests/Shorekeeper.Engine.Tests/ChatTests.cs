using System.Net;
using System.Net.Http.Json;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Monitoring;

namespace Shorekeeper.Engine.Tests;

/// <summary>Messages between real engines over HTTPS (docs/15-chat.md).</summary>
public sealed class ChatTests : IAsyncLifetime
{
    private TestNode a = null!;
    private TestNode b = null!;

    public async ValueTask InitializeAsync()
    {
        a = await TestNode.StartAsync("A");
        b = await TestNode.StartAsync("B");
        await a.ConnectAsync(b);
    }

    public async ValueTask DisposeAsync()
    {
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Text_arrives_unread_and_the_sender_sees_it_delivered()
    {
        var received = new List<ChatMessage>();
        b.Chat.Received += (_, m) => received.Add(m);

        ChatMessage sent = await a.Chat.SendTextAsync(b.Id, "  Anh gửi lại file báo cáo nhé ", Ct);

        await WaitUntil(() => received.Count == 1);
        ChatMessage got = Assert.Single(await b.Chat.GetMessagesAsync(a.Id, Ct));
        Assert.Equal((sent.Id, "Anh gửi lại file báo cáo nhé", ChatKinds.Text, false, ChatMessageState.Unread), (got.Id, got.Text, got.Kind, got.Outgoing, got.State));
        Assert.Equal(sent.SentAt, got.SentAt);
        ChatConversation conversation = Assert.Single(await b.Chat.GetConversationsAsync(Ct));
        Assert.Equal((a.Id, 1, sent.Id), (conversation.Peer, conversation.Unread, conversation.Last.Id));

        await WaitUntil(() => State(a, b, sent.Id) == ChatMessageState.Delivered);
    }

    [Fact]
    public async Task Reading_tells_the_sender()
    {
        ChatMessage first = await a.Chat.SendTextAsync(b.Id, "một", Ct);
        ChatMessage second = await a.Chat.SendTextAsync(b.Id, "hai", Ct);
        await WaitUntil(() => b.Chat.GetMessagesAsync(a.Id, Ct).Result.Count == 2);

        await b.Chat.MarkReadAsync(a.Id, Ct);

        Assert.Equal(0, Assert.Single(await b.Chat.GetConversationsAsync(Ct)).Unread);
        Assert.All(await b.Chat.GetMessagesAsync(a.Id, Ct), m => Assert.Equal(ChatMessageState.Read, m.State));
        await WaitUntil(() => State(a, b, first.Id) == ChatMessageState.Read && State(a, b, second.Id) == ChatMessageState.Read);
    }

    [Fact]
    public async Task Messages_wait_for_an_offline_peer_and_arrive_in_order()
    {
        a.Directory.Remove(b.Id); // A does not see B and knows no address for it
        string[] texts = ["một", "hai", "ba"];
        foreach (string text in texts)
        {
            await a.Chat.SendTextAsync(b.Id, text, Ct);
        }

        await Task.Delay(500, Ct);
        Assert.All(await a.Chat.GetMessagesAsync(b.Id, Ct), m => Assert.Equal(ChatMessageState.Pending, m.State));
        Assert.Empty(await b.Chat.GetMessagesAsync(a.Id, Ct));

        a.Sees(b);

        await WaitUntil(() => b.Chat.GetMessagesAsync(a.Id, Ct).Result.Count == 3);
        Assert.Equal(texts, (await b.Chat.GetMessagesAsync(a.Id, Ct)).Select(m => m.Text));
        await WaitUntil(() => a.Chat.GetMessagesAsync(b.Id, Ct).Result.All(m => m.State == ChatMessageState.Delivered));
    }

    [Fact]
    public async Task A_message_sent_twice_is_kept_once()
    {
        int received = 0;
        b.Chat.Received += (_, _) => Interlocked.Increment(ref received);
        var body = new ChatMessageBody(Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow, ChatKinds.Text, "xin chào");

        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(a, b, "chat/messages", body));
        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(a, b, "chat/messages", body));

        Assert.Single(await b.Chat.GetMessagesAsync(a.Id, Ct));
        Assert.Equal(1, received);
    }

    [Fact]
    public async Task A_kind_from_a_newer_version_is_kept_without_text()
    {
        var body = new ChatMessageBody(Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow, "sticker", "ignored");

        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(a, b, "chat/messages", body));

        ChatMessage got = Assert.Single(await b.Chat.GetMessagesAsync(a.Id, Ct));
        Assert.Equal(("sticker", null), (got.Kind, got.Text));
    }

    [Theory]
    [InlineData("not-an-id", "text", "hi")]
    [InlineData(null, "text", "")]
    [InlineData(null, "text", null)]
    [InlineData(null, "", "hi")]
    public async Task Malformed_messages_are_refused(string? id, string kind, string? text)
    {
        var body = new ChatMessageBody(id ?? Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow, kind, text);

        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(a, b, "chat/messages", body));
        Assert.Empty(await b.Chat.GetMessagesAsync(a.Id, Ct));
    }

    [Fact]
    public async Task Too_long_text_is_not_sent()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => a.Chat.SendTextAsync(b.Id, new string('x', ChatService.MaxTextLength + 1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => a.Chat.SendTextAsync(b.Id, "   ", Ct));
        Assert.Empty(await a.Chat.GetConversationsAsync(Ct));
    }

    [Fact]
    public async Task A_clock_ahead_does_not_put_a_message_in_the_future()
    {
        var body = new ChatMessageBody(Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow.AddDays(1), ChatKinds.Text, "từ tương lai");

        await PostAsync(a, b, "chat/messages", body);

        Assert.True(Assert.Single(await b.Chat.GetMessagesAsync(a.Id, Ct)).SentAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Peers_that_are_not_contacts_cannot_send()
    {
        await using TestNode stranger = await TestNode.StartAsync("C");
        stranger.Sees(b);
        var body = new ChatMessageBody(Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow, ChatKinds.Text, "hi");

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(stranger, b, "chat/messages", body));
        Assert.Empty(await b.Chat.GetConversationsAsync(Ct));
    }

    [Fact]
    public async Task A_message_to_a_peer_that_blocked_us_fails_and_can_be_sent_again()
    {
        await b.Trust.BlockAsync(a.Id, "A", "HOST-A", Ct);
        ChatMessage sent = await a.Chat.SendTextAsync(b.Id, "còn đó không?", Ct);
        await WaitUntil(() => State(a, b, sent.Id) == ChatMessageState.Failed);

        await b.Trust.SetTrustedAsync(a.Id, "A", "HOST-A", Ct);
        await a.Chat.RetryAsync(b.Id, sent.Id, Ct);

        await WaitUntil(() => State(a, b, sent.Id) == ChatMessageState.Delivered);
        Assert.Single(await b.Chat.GetMessagesAsync(a.Id, Ct));
    }

    [Fact]
    public async Task A_voice_message_arrives_whole_with_its_length_and_waveform()
    {
        VoiceNote note = Voice(seconds: 1);

        ChatMessage sent = await a.Chat.SendVoiceAsync(b.Id, note, Ct);

        await WaitUntil(() => b.Chat.GetMessagesAsync(a.Id, Ct).Result.Count == 1);
        ChatMessage got = Assert.Single(await b.Chat.GetMessagesAsync(a.Id, Ct));
        Assert.Equal((sent.Id, ChatKinds.Voice, 1000), (got.Id, got.Kind, got.DurationMs));
        Assert.Equal(note.Wave, got.Wave);
        Assert.Equal(note.Ogg, await File.ReadAllBytesAsync(got.VoicePath!, Ct));
        Assert.Equal(note.Ogg, await File.ReadAllBytesAsync(sent.VoicePath!, Ct));
        await WaitUntil(() => State(a, b, sent.Id) == ChatMessageState.Delivered);
    }

    [Theory]
    [InlineData("no audio")]
    [InlineData("not ogg")]
    [InlineData("too long")]
    [InlineData("too big")]
    public async Task Malformed_voice_messages_are_refused(string fault)
    {
        VoiceNote note = Voice(seconds: 1);
        var body = new ChatMessageBody(Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow, ChatKinds.Voice, null, note.DurationMs, note.Wave, note.Ogg);
        body = fault switch
        {
            "no audio" => body with { Audio = null },
            "not ogg" => body with { Audio = "RIFF....WAVE"u8.ToArray() },
            "too long" => body with { DurationMs = VoiceRecorder.MaxDurationMs + 60_000 },
            _ => body with { Audio = [.. "OggS"u8, .. new byte[ChatService.MaxVoiceBytes]] },
        };

        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(a, b, "chat/messages", body));
        Assert.Empty(await b.Chat.GetMessagesAsync(a.Id, Ct));
    }

    [Fact]
    public async Task Deleting_takes_messages_and_their_voice_files_away_here_only()
    {
        ChatMessage text = await a.Chat.SendTextAsync(b.Id, "giữ lại", Ct);
        ChatMessage voice = await a.Chat.SendVoiceAsync(b.Id, Voice(seconds: 1), Ct);
        await WaitUntil(() => b.Chat.GetMessagesAsync(a.Id, Ct).Result.Count == 2);

        await a.Chat.DeleteAsync(b.Id, [voice.Id], Ct);

        Assert.Equal([text.Id], (await a.Chat.GetMessagesAsync(b.Id, Ct)).Select(m => m.Id));
        Assert.False(File.Exists(voice.VoicePath));
        Assert.Equal(2, (await b.Chat.GetMessagesAsync(a.Id, Ct)).Count);
    }

    [Fact]
    public async Task Deleting_a_conversation_clears_it_and_its_pending_receipt()
    {
        await a.Chat.SendTextAsync(b.Id, "một", Ct);
        ChatMessage voice = await a.Chat.SendVoiceAsync(b.Id, Voice(seconds: 1), Ct);
        await WaitUntil(() => b.Chat.GetMessagesAsync(a.Id, Ct).Result.Count == 2);
        b.Directory.Remove(a.Id);
        await b.Chat.MarkReadAsync(a.Id, Ct);
        string received = (await b.Chat.GetMessagesAsync(a.Id, Ct)).Single(m => m.Id == voice.Id).VoicePath!;

        await b.Chat.DeleteConversationAsync(a.Id, Ct);
        b.Sees(a);

        Assert.Empty(await b.Chat.GetConversationsAsync(Ct));
        Assert.False(File.Exists(received));
        // The receipt waiting for A went with it: A's messages stay delivered, not read.
        await Task.Delay(500, Ct);
        Assert.All(await a.Chat.GetMessagesAsync(b.Id, Ct), m => Assert.Equal(ChatMessageState.Delivered, m.State));
    }

    [Fact]
    public async Task Messages_are_counted_once_on_each_side()
    {
        ChatMessage sent = await a.Chat.SendTextAsync(b.Id, "đếm", Ct);
        await WaitUntil(() => State(a, b, sent.Id) == ChatMessageState.Delivered);
        var body = new ChatMessageBody(sent.Id, sent.SentAt, ChatKinds.Text, "đếm");
        await PostAsync(a, b, "chat/messages", body);

        UsageSummary mine = a.Get<UsageStatsService>().Summary();
        UsageSummary theirs = b.Get<UsageStatsService>().Summary();
        Assert.Equal((1, 0), (mine.Total.SentMessages, mine.Total.ReceivedMessages));
        Assert.Equal((0, 1), (theirs.Today.SentMessages, theirs.Today.ReceivedMessages));
    }

    [Fact]
    public void Presence_says_messages_are_taken()
    {
        PresencePacket packet = a.Get<LocalPresence>().CreatePacket(PresencePacketTypes.Announce);

        Assert.Contains(PresenceCapabilities.Chat, packet.Capabilities);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static ChatMessageState? State(TestNode sender, TestNode peer, string id) =>
        sender.Chat.GetMessagesAsync(peer.Id, Ct).Result.SingleOrDefault(m => m.Id == id)?.State;

    private static async Task<HttpStatusCode> PostAsync<T>(TestNode from, TestNode to, string path, T body)
    {
        using HttpResponseMessage response = await from.Client.SendAsync(
            to.Id, u => new HttpRequestMessage(HttpMethod.Post, new Uri(u, path)) { Content = JsonContent.Create(body) }, TimeSpan.FromSeconds(10), Ct);
        return response.StatusCode;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            await Task.Delay(50, Ct);
        }
    }
}
