using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Audio;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Engine.Tests;

/// <summary>"Phát âm thanh" between real engines: HTTPS to join, sealed UDP for the sound (docs/05-protocol.md §4.7).</summary>
public sealed class AudioTests : IAsyncLifetime
{
    private readonly FakeCapture speakers = new();
    private readonly FakeOutput headphones = new();
    private TestNode pc = null!;
    private TestNode phone = null!;

    public async ValueTask InitializeAsync()
    {
        pc = await TestNode.StartAsync("PC", services: s => s.AddSingleton<IAudioCapture>(speakers));
        phone = await TestNode.StartAsync("Phone", services: s => s.AddSingleton<IAudioOutput>(headphones));
    }

    public async ValueTask DisposeAsync()
    {
        await pc.DisposeAsync();
        await phone.DisposeAsync();
    }

    [Fact]
    public async Task A_contact_hears_the_sound_while_playing_is_on()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPlaying(true);

        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);

        await WaitUntil(() => headphones.SoundFrames > 20);
        Assert.Equal(phone.Id, Assert.Single(pc.Audio.Listeners).PeerId);
        Assert.Equal(pc.Id, phone.Audio.ListeningTo);
        Assert.True(speakers.IsRunning);
    }

    [Fact]
    public async Task Listening_reports_its_stats_every_second_with_the_ping()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPlaying(true);
        var answered = new TaskCompletionSource<AudioListeningStats>();
        phone.Audio.StatsChanged += (_, stats) =>
        {
            if (stats.Ping is not null)
            {
                answered.TrySetResult(stats);
            }
        };

        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);
        AudioListeningStats shown = await answered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.True(shown.Ping < TimeSpan.FromSeconds(1));
        Assert.Equal(AudioLatency.Low, shown.Latency);
        Assert.Equal(30, shown.BaseMs);
        Assert.True(shown.Kbps > 0);
        Assert.Equal(0, shown.Lost);
    }

    [Fact]
    public async Task Keep_alives_hold_the_session_past_the_timeouts()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPlaying(true);
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);

        // Longer than both sides wait for each other (6 and 8 seconds).
        await Task.Delay(TimeSpan.FromSeconds(9), Ct);

        Assert.Single(pc.Audio.Listeners);
        Assert.Equal(pc.Id, phone.Audio.ListeningTo);
        int heard = headphones.SoundFrames;
        await WaitUntil(() => headphones.SoundFrames > heard + 20);
    }

    [Fact]
    public async Task Stopping_to_listen_frees_the_speakers()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPlaying(true);
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);
        await WaitUntil(() => headphones.SoundFrames > 0);

        await phone.Audio.StopListeningAsync();

        Assert.Null(phone.Audio.ListeningTo);
        Assert.False(headphones.IsRunning);
        await WaitUntil(() => pc.Audio.Listeners.Count == 0 && !speakers.IsRunning);
        Assert.True(pc.Audio.IsPlaying);
    }

    [Fact]
    public async Task Turning_playing_off_ends_the_listeners_session()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPlaying(true);
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);
        string? notice = null;
        phone.Audio.Notice += (_, message) => notice = message;

        pc.Audio.SetPlaying(false);

        await WaitUntil(() => phone.Audio.ListeningTo is null && notice is not null);
        Assert.False(speakers.IsRunning);
        Assert.False(headphones.IsRunning);
    }

    [Fact]
    public async Task Nobody_can_listen_while_playing_is_off()
    {
        await pc.ConnectAsync(phone);

        var error = await Assert.ThrowsAsync<PeerApiException>(() => phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct));

        Assert.Equal(404, error.Status);
        Assert.Null(phone.Audio.ListeningTo);
    }

    [Fact]
    public async Task Only_contacts_may_listen()
    {
        phone.Sees(pc);
        pc.Audio.SetPlaying(true);

        var error = await Assert.ThrowsAsync<PeerApiException>(() => phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct));

        Assert.Equal(403, error.Status);
        Assert.Empty(pc.Audio.Listeners);
    }

    [Fact]
    public void Playing_is_announced_in_presence()
    {
        var presence = pc.Get<ILocalPresence>();

        pc.Audio.SetPlaying(true);
        Assert.Contains(PresenceCapabilities.Audio, presence.CreatePacket(PresencePacketTypes.Heartbeat).Capabilities);

        pc.Audio.SetPlaying(false);
        Assert.DoesNotContain(PresenceCapabilities.Audio, presence.CreatePacket(PresencePacketTypes.Heartbeat).Capabilities);
    }

    [Fact]
    public async Task A_password_is_asked_for_and_remembered_once_it_works()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPassword("cá voi");
        pc.Audio.SetPlaying(true);

        var missing = await Assert.ThrowsAsync<PeerApiException>(() => phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct));
        var wrong = await Assert.ThrowsAsync<PeerApiException>(() => phone.Audio.ListenAsync(pc.Id, "cá mập", Ct));
        Assert.Equal((403, ApiErrorCodes.PasswordRequired), (missing.Status, missing.Code));
        Assert.Equal((403, ApiErrorCodes.WrongPassword), (wrong.Status, wrong.Code));
        Assert.Null(phone.Audio.ListeningTo);

        await phone.Audio.ListenAsync(pc.Id, "cá voi", Ct);
        await WaitUntil(() => headphones.SoundFrames > 0);
        await phone.Audio.StopListeningAsync();

        // Next time the remembered password is sent by itself.
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);
        Assert.Equal(pc.Id, phone.Audio.ListeningTo);
    }

    [Fact]
    public async Task A_changed_password_makes_the_remembered_one_forgotten()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPassword("một");
        pc.Audio.SetPlaying(true);
        await phone.Audio.ListenAsync(pc.Id, "một", Ct);
        await phone.Audio.StopListeningAsync();

        pc.Audio.SetPassword("hai");

        var refused = await Assert.ThrowsAsync<PeerApiException>(() => phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct));
        Assert.Equal(ApiErrorCodes.WrongPassword, refused.Code);
        Assert.Null(phone.Get<AudioPasswords>().Get(pc.Id));
    }

    [Fact]
    public async Task Too_many_wrong_passwords_lock_the_caller_out_for_a_while()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPassword("đúng");
        pc.Audio.SetPlaying(true);
        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<PeerApiException>(() => phone.Audio.ListenAsync(pc.Id, "sai", Ct));
        }

        // Even the right one waits now.
        var locked = await Assert.ThrowsAsync<PeerApiException>(() => phone.Audio.ListenAsync(pc.Id, "đúng", Ct));

        Assert.Equal(429, locked.Status);
        Assert.Empty(pc.Audio.Listeners);
    }

    [Fact]
    public void A_password_is_announced_while_playing()
    {
        var presence = pc.Get<ILocalPresence>();
        pc.Audio.SetPassword("khóa");
        Assert.DoesNotContain(PresenceCapabilities.AudioLocked, presence.CreatePacket(PresencePacketTypes.Heartbeat).Capabilities);

        pc.Audio.SetPlaying(true);
        Assert.Contains(PresenceCapabilities.AudioLocked, presence.CreatePacket(PresencePacketTypes.Heartbeat).Capabilities);

        pc.Audio.SetPassword(null);
        Assert.False(pc.Audio.HasPassword);
        Assert.DoesNotContain(PresenceCapabilities.AudioLocked, presence.CreatePacket(PresencePacketTypes.Heartbeat).Capabilities);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 5s.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Speakers playing a 440 Hz tone, one frame every 10 ms.</summary>
    private sealed class FakeCapture : IAudioCapture
    {
        private volatile bool running;

        public bool IsRunning => running;

        public IDisposable Start(AudioFrameHandler onFrame)
        {
            running = true;
            var thread = new Thread(() =>
            {
                float[] frame = new float[AudioStreamFormat.FrameValues];
                for (int n = 0; running; n++)
                {
                    for (int i = 0; i < AudioStreamFormat.FrameSamples; i++)
                    {
                        frame[2 * i] = frame[(2 * i) + 1] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * ((n * AudioStreamFormat.FrameSamples) + i) / AudioStreamFormat.SampleRate);
                    }

                    onFrame(frame);
                    Thread.Sleep(AudioStreamFormat.FrameMilliseconds);
                }
            }) { IsBackground = true };
            thread.Start();
            return new Stop(() =>
            {
                running = false;
                thread.Join();
            });
        }
    }

    /// <summary>Headphones that count the frames with sound in them.</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        private volatile bool running;
        private int soundFrames;

        public bool IsRunning => running;

        public int SoundFrames => Volatile.Read(ref soundFrames);

        public IDisposable Start(AudioFrameReader read, string title, Action stopRequested)
        {
            running = true;
            var thread = new Thread(() =>
            {
                short[] pcm = new short[AudioStreamFormat.FrameValues];
                while (running)
                {
                    read(pcm);
                    if (pcm.Any(v => Math.Abs((int)v) > 1000))
                    {
                        Interlocked.Increment(ref soundFrames);
                    }

                    Thread.Sleep(AudioStreamFormat.FrameMilliseconds);
                }
            }) { IsBackground = true };
            thread.Start();
            return new Stop(() =>
            {
                running = false;
                thread.Join();
            });
        }
    }

    private sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
