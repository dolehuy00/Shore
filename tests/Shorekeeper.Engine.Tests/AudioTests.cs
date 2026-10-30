using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Audio;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Tests;

/// <summary>"Phát âm thanh" between real engines: HTTPS to join, sealed UDP for the sound (docs/05-protocol.md §4.7).</summary>
public sealed class AudioTests : IAsyncLifetime
{
    private readonly FakeCapture speakers = new();
    private readonly FakeOutput headphones = new();
    private readonly FakeMediaKeys keys = new();
    private TestNode pc = null!;
    private TestNode phone = null!;

    public async ValueTask InitializeAsync()
    {
        pc = await TestNode.StartAsync("PC", services: s => s.AddSingleton<IAudioCapture>(speakers).AddSingleton<IMediaKeys>(keys));
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
    public async Task The_listener_sets_its_own_volume_and_it_is_remembered_for_that_source()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPlaying(true);
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);
        await WaitUntil(() => headphones.Peak > 8000);
        int full = headphones.Peak;

        phone.Audio.SetListenVolume(50);

        Assert.Equal(50, phone.Audio.ListenVolume);
        await WaitUntil(() => headphones.Peak is > 0 and < 6000);
        Assert.InRange(headphones.Peak, full * 4 / 10, full * 6 / 10);
        // The source plays on as before: only this device hears it softer.
        Assert.True(speakers.IsRunning);

        // Saved within a second, and used the next time this source is heard.
        await WaitUntil(() => phone.Get<SettingsService>().Current.ListenVolumes.GetValueOrDefault(pc.Id.Value) == 50);
        await phone.Audio.StopListeningAsync();
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);
        Assert.Equal(50, phone.Audio.ListenVolume);

        phone.Audio.SetListenVolume(0);
        await WaitUntil(() => headphones.Peak == 0);

        // Silent is not kept for next time: listening again is meant to be heard.
        await WaitUntil(() => phone.Get<SettingsService>().Current.ListenVolumes.GetValueOrDefault(pc.Id.Value) == 0);
        await phone.Audio.StopListeningAsync();
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);
        Assert.Equal(100, phone.Audio.ListenVolume);
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
    public async Task A_listener_presses_media_keys_on_the_source()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPlaying(true);
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);

        Assert.True(phone.Audio.CanControlListened);
        await phone.Audio.PressAsync(MediaKey.PlayPause, Ct);
        await phone.Audio.PressAsync(MediaKey.VolumeUp, Ct);

        Assert.Equal([MediaKey.PlayPause, MediaKey.VolumeUp], keys.Pressed);
    }

    [Fact]
    public async Task Only_a_listener_in_its_session_presses_keys()
    {
        await pc.ConnectAsync(phone);
        pc.Audio.SetPlaying(true);
        var client = phone.Get<PeerClient>();
        async Task<HttpStatusCode> PressAsync(string session, string key)
        {
            using HttpResponseMessage response = await client.SendAsync(
                pc.Id, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"audio/listeners/{session}/keys")) { Content = JsonContent.Create(new AudioKeyBody(key)) },
                TimeSpan.FromSeconds(5), Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await PressAsync("notlistening", "play-pause"));
        await phone.Audio.ListenAsync(pc.Id, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, await PressAsync("notlistening", "self-destruct"));
        Assert.Equal(HttpStatusCode.NotFound, await PressAsync("someoneelse", "play-pause"));

        Assert.Empty(keys.Pressed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pc.Audio.PressAsync(MediaKey.Next, Ct));
    }

    [Fact]
    public async Task A_source_without_media_keys_cannot_be_controlled()
    {
        await using TestNode other = await TestNode.StartAsync("Other PC", services: s => s.AddSingleton<IAudioCapture>(new FakeCapture()));
        await other.ConnectAsync(phone);
        other.Audio.SetPlaying(true);

        await phone.Audio.ListenAsync(other.Id, cancellationToken: Ct);

        Assert.False(phone.Audio.CanControlListened);
        await Assert.ThrowsAsync<InvalidOperationException>(() => phone.Audio.PressAsync(MediaKey.PlayPause, Ct));
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

    private sealed class FakeMediaKeys : IMediaKeys
    {
        private readonly List<MediaKey> pressed = [];

        public IReadOnlyList<MediaKey> Pressed
        {
            get
            {
                lock (pressed)
                {
                    return [.. pressed];
                }
            }
        }

        public void Press(MediaKey key)
        {
            lock (pressed)
            {
                pressed.Add(key);
            }
        }
    }

    /// <summary>Headphones that count the frames with sound in them.</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        private volatile bool running;
        private int soundFrames;
        private int peak;

        public bool IsRunning => running;

        public int SoundFrames => Volatile.Read(ref soundFrames);

        /// <summary>The loudest sample of the last frame played.</summary>
        public int Peak => Volatile.Read(ref peak);

        public IDisposable Start(AudioFrameReader read, string title, Action stopRequested, Action<MediaKey>? keyRequested = null, string? device = null)
        {
            running = true;
            var thread = new Thread(() =>
            {
                short[] pcm = new short[AudioStreamFormat.FrameValues];
                while (running)
                {
                    read(pcm);
                    Volatile.Write(ref peak, pcm.Max(v => Math.Abs((int)v)));
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
