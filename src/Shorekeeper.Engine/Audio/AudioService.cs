using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Audio;

/// <summary>A contact listening to this device's sound.</summary>
public sealed record AudioListener(string SessionId, DeviceId PeerId, string Name);

/// <summary>How listening goes ("Thống kê", docs/10-ux.md §12), measured every second.</summary>
/// <param name="Latency">The "Độ trễ khi nghe" this session started with.</param>
/// <param name="BaseMs">What its buffer holds when the network does well.</param>
/// <param name="Ping">Round trip to the source, timed on the last keep-alive it answered; null until one is answered.</param>
/// <param name="BufferMs">Sound in hand now.</param>
/// <param name="TargetMs">What the buffer aims to hold.</param>
/// <param name="JitterMs">How much later than the fastest the slow packets (99th percentile) came, over the last second.</param>
/// <param name="LossPercent">Packets lost on the way over the last 10 s.</param>
/// <param name="Lost">Packets lost on the way since listening started.</param>
/// <param name="Saved">Of those, the ones played anyway: from the next packet's copy, or resent in time.</param>
/// <param name="GapMs">Sound that could not play since listening started: guessed, or silence while waiting.</param>
/// <param name="RecentGapMs">The same over the last 10 s.</param>
/// <param name="Kbps">Received over the last second, resends included.</param>
public sealed record AudioListeningStats(
    AudioLatency Latency, int BaseMs, TimeSpan? Ping, int BufferMs, int TargetMs, double JitterMs, double LossPercent, int Lost, int Saved, int GapMs, int RecentGapMs, int Kbps);

public enum AudioJoinResult
{
    Joined,
    Invalid,

    /// <summary>"Phát âm thanh" is off, or this device cannot capture its sound.</summary>
    NotPlaying,
    Full,

    /// <summary>The source has a password and the caller sent none.</summary>
    PasswordRequired,
    WrongPassword,

    /// <summary>Too many wrong passwords from this caller lately.</summary>
    TooManyAttempts,

    /// <summary>The source no longer lets this caller listen ("Không cho nghe"), until it plays again.</summary>
    Removed,
}

/// <summary>
/// "Phát âm thanh" (docs/05-protocol.md §4.7). A computer or phone turns it on; contacts that want to listen ask to join, each
/// with a fresh key and a UDP port of its own, and the source's password when it has one. The sound is captured only while someone listens, encoded once and sealed
/// for each listener. Listeners prove they are still there every second; either side stops after a few silent seconds.
/// A listener that misses packets asks for them again, and the source resends what it still has from the last 3 s.
/// Where the source can press media keys (Windows), a listener it lets ("Cho điều khiển", per listener) may play, pause,
/// skip and change the volume there. The source can also stop playing to one listener ("Ngừng phát"), or keep one out
/// until it plays again ("Không cho nghe").
/// </summary>
public sealed partial class AudioService : IDisposable
{
    public const int MaxListeners = 4;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ListenerGone = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SourceGone = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WrongPasswordWindow = TimeSpan.FromMinutes(1);
    private const int MaxWrongPasswords = 5;

    private readonly PeerClient client;
    private readonly TrustStore trust;
    private readonly LocalPresence presence;
    private readonly SettingsService settings;
    private readonly AudioPasswords passwords;
    private readonly ILogger<AudioService> logger;
    private readonly IAudioCapture? capture;
    private readonly IAudioOutput? output;
    private readonly IMediaKeys? mediaKeys;
    private readonly Lock gate = new();
    private readonly ITimer timer;
    private readonly Dictionary<DeviceId, (int Count, long Since)> wrongPasswords = [];
    private Broadcast? broadcast;
    private Listening? listening;

    /// <summary>Listeners kept out with "Không cho nghe": they may not listen again until playing is turned off.</summary>
    private readonly HashSet<DeviceId> removed = [];

    /// <summary>A level set while listening, saved on the next tick: a slider being dragged does not write each step.</summary>
    private (DeviceId Source, int Percent)? unsavedVolume;

    public AudioService(
        PeerClient client,
        TrustStore trust,
        LocalPresence presence,
        SettingsService settings,
        AudioPasswords passwords,
        TimeProvider timeProvider,
        ILogger<AudioService> logger,
        IAudioCapture? capture = null,
        IAudioOutput? output = null,
        IMediaKeys? mediaKeys = null)
    {
        this.client = client;
        this.trust = trust;
        this.presence = presence;
        this.settings = settings;
        this.passwords = passwords;
        this.logger = logger;
        this.capture = capture;
        this.output = output;
        this.mediaKeys = mediaKeys;
        timer = timeProvider.CreateTimer(_ => OnTick(), null, Tick, Tick);
    }

    /// <summary>Playing, listeners or listening changed. Raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Every second while listening, from the service's timer.</summary>
    public event EventHandler<AudioListeningStats>? StatsChanged;

    /// <summary>Something the user should read: a listener could not be served, the sound we listened to stopped.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>This device can play its sound for others (it can capture its speakers).</summary>
    public bool CanPlay => capture is not null;

    /// <summary>Media keys can be pressed here, so "Cho điều khiển" can be offered to listeners.</summary>
    public bool CanBeControlled => mediaKeys is not null;

    /// <summary>"Cho điều khiển": <paramref name="listener"/> may press media keys here (remembered per device).</summary>
    public bool AllowsControl(DeviceId listener) =>
        mediaKeys is not null && settings.Current.AudioControllers.Contains(listener.Value);

    /// <summary>The device we listen to lets us press its media keys (<see cref="PressAsync"/>).</summary>
    public bool CanControlListened
    {
        get
        {
            lock (gate)
            {
                return listening?.CanControl == true;
            }
        }
    }

    /// <summary>This device can listen to another's sound.</summary>
    public bool CanListen => output is not null;

    /// <summary>"Phát âm thanh" is on.</summary>
    public bool IsPlaying => presence.PlayingAudio;

    /// <summary>Listeners must give the password set with <see cref="SetPassword"/>.</summary>
    public bool HasPassword => settings.User.AudioPasswordHash is not null;

    public IReadOnlyList<AudioListener> Listeners
    {
        get
        {
            lock (gate)
            {
                return broadcast is null ? [] : [.. broadcast.Listeners.Select(l => l.Info)];
            }
        }
    }

    /// <summary>The device we listen to, or null.</summary>
    public DeviceId? ListeningTo
    {
        get
        {
            lock (gate)
            {
                return listening?.PeerId;
            }
        }
    }

    // ───────────── Playing ─────────────

    /// <summary>Turns "Phát âm thanh" on or off; off ends every listener's session.</summary>
    public void SetPlaying(bool on)
    {
        if (on && !CanPlay)
        {
            throw new InvalidOperationException(L.T("Máy này không phát được âm thanh."));
        }

        Broadcast? ended = null;
        lock (gate)
        {
            if (on == presence.PlayingAudio)
            {
                return;
            }

            presence.PlayingAudio = on;
            if (!on)
            {
                (ended, broadcast) = (broadcast, null);
                removed.Clear();
            }
        }

        logger.LogInformation("Playing sound for contacts {State}", on ? "on" : "off");
        if (ended is not null)
        {
            foreach (Listener listener in ended.Listeners)
            {
                _ = TellEndedAsync(listener.Info);
            }

            ended.Dispose();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets or (with null) removes the password; who listens already keeps listening.</summary>
    public void SetPassword(string? password)
    {
        settings.Update(s => s with { AudioPasswordHash = string.IsNullOrEmpty(password) ? null : AudioPassword.Hash(password) });
        lock (gate)
        {
            wrongPasswords.Clear();
        }

        logger.LogInformation("Password for playing sound {State}", password is null ? "removed" : "set");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Cho điều khiển" for one listener; remembered, and a listener listening now learns at once.</summary>
    public void SetAllowControl(DeviceId listener, bool allowed)
    {
        settings.Update(s =>
        {
            var controllers = new HashSet<string>(s.AudioControllers ?? [], StringComparer.Ordinal);
            if (allowed)
            {
                controllers.Add(listener.Value);
            }
            else
            {
                controllers.Remove(listener.Value);
            }

            return s with { AudioControllers = controllers.Count == 0 ? null : [.. controllers.Order(StringComparer.Ordinal)] };
        });
        logger.LogInformation("{Peer} may control: {Allowed}", listener, allowed);
        foreach (AudioListener info in Listeners.Where(l => l.PeerId == listener))
        {
            _ = TellControlAsync(info, AllowsControl(listener));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// "Ngừng phát" (<paramref name="keepOut"/> false): stops playing to <paramref name="listener"/>, which may listen again.
    /// "Không cho nghe" (true): also keeps it out until playing is turned off.
    /// </summary>
    public void StopListener(DeviceId listener, bool keepOut)
    {
        AudioListener[] stopped;
        lock (gate)
        {
            if (!presence.PlayingAudio)
            {
                return;
            }

            if (keepOut)
            {
                removed.Add(listener);
            }

            stopped = broadcast is null ? [] : [.. broadcast.Listeners.Where(l => l.Info.PeerId == listener).Select(l => l.Info)];
        }

        RemoveListener(l => l.Info.PeerId == listener);
        foreach (AudioListener info in stopped)
        {
            _ = TellEndedAsync(info, keepOut ? AudioEndReasons.Removed : AudioEndReasons.Stopped);
        }

        logger.LogInformation("Stopped playing to {Peer} ({How})", listener, keepOut ? "kept out" : "may come back");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal AudioJoinResult HandleJoin(DeviceId caller, IPAddress address, AudioListenBody body)
    {
        if (!IsValidSessionId(body.SessionId) || body.Port is < 1 or > 65535 || Key(body.Key) is not { } key)
        {
            return AudioJoinResult.Invalid;
        }

        if (!presence.PlayingAudio || capture is null)
        {
            return AudioJoinResult.NotPlaying;
        }

        lock (gate)
        {
            if (removed.Contains(caller))
            {
                return AudioJoinResult.Removed;
            }
        }

        if (CheckPassword(caller, body.Password) is { } refused)
        {
            logger.LogInformation("{Peer} may not listen: {Reason}", caller, refused);
            return refused;
        }

        var info = new AudioListener(body.SessionId, caller, NameOf(caller));
        var endpoint = new IPEndPoint(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address, body.Port);
        lock (gate)
        {
            if (!presence.PlayingAudio || capture is null)
            {
                return AudioJoinResult.NotPlaying;
            }

            // Asking again (the app restarted) replaces the earlier session.
            broadcast?.Remove(caller);
            if (broadcast?.Listeners.Count >= MaxListeners)
            {
                return AudioJoinResult.Full;
            }

            if (broadcast is null)
            {
                try
                {
                    broadcast = new Broadcast(capture, logger);
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(ex, "Cannot capture the speakers");
                    Notice?.Invoke(this, ex.Message);
                    return AudioJoinResult.NotPlaying;
                }
            }

            broadcast.Add(new Listener(info, endpoint, key));
        }

        logger.LogInformation("{Peer} listens from {Endpoint}", caller, endpoint);
        Changed?.Invoke(this, EventArgs.Empty);
        return AudioJoinResult.Joined;
    }

    /// <summary>A listener pressed a media key; it must be listening now, in that session.</summary>
    internal bool HandleKey(DeviceId caller, string sessionId, MediaKey key)
    {
        bool listens;
        lock (gate)
        {
            listens = broadcast?.Listeners.Any(l => l.Info.PeerId == caller && l.Info.SessionId == sessionId) == true;
        }

        if (!listens || !AllowsControl(caller))
        {
            return false;
        }

        logger.LogInformation("{Peer} pressed {Key}", caller, key);
        mediaKeys!.Press(key);
        return true;
    }

    internal void HandleLeave(DeviceId caller, string sessionId)
    {
        if (RemoveListener(l => l.Info.PeerId == caller && l.Info.SessionId == sessionId))
        {
            logger.LogInformation("{Peer} stopped listening", caller);
        }
    }

    /// <returns>Null when the caller may listen.</returns>
    private AudioJoinResult? CheckPassword(DeviceId caller, string? password)
    {
        if (settings.User.AudioPasswordHash is not { } hash)
        {
            return null;
        }

        lock (gate)
        {
            if (wrongPasswords.TryGetValue(caller, out (int Count, long Since) wrong)
                && wrong.Count >= MaxWrongPasswords && Stopwatch.GetElapsedTime(wrong.Since) < WrongPasswordWindow)
            {
                return AudioJoinResult.TooManyAttempts;
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            return AudioJoinResult.PasswordRequired;
        }

        // Tens of milliseconds of key derivation: outside the lock.
        bool right = AudioPassword.Verify(password, hash);
        lock (gate)
        {
            if (right)
            {
                wrongPasswords.Remove(caller);
                return null;
            }

            wrongPasswords[caller] = wrongPasswords.TryGetValue(caller, out (int Count, long Since) wrong)
                && Stopwatch.GetElapsedTime(wrong.Since) < WrongPasswordWindow
                    ? (wrong.Count + 1, wrong.Since)
                    : (1, Stopwatch.GetTimestamp());
            return AudioJoinResult.WrongPassword;
        }
    }

    // ───────────── Listening ─────────────

    /// <summary>
    /// Starts listening to <paramref name="peer"/>, which must have "Phát âm thanh" on. Without
    /// <paramref name="password"/> the one that worked last time for this peer is sent; a password that works is kept for
    /// next time, one the peer refuses is forgotten.
    /// </summary>
    /// <exception cref="InvalidOperationException">This device cannot listen, or already listens.</exception>
    /// <exception cref="PeerUnreachableException">The peer could not be reached.</exception>
    /// <exception cref="PeerApiException">
    /// The peer refused: 404 when it does not play, 503 when it is full, 403 with <see cref="ApiErrorCodes.PasswordRequired"/>
    /// or <see cref="ApiErrorCodes.WrongPassword"/>, 429 after too many wrong passwords.
    /// </exception>
    public async Task ListenAsync(DeviceId peer, string? password = null, CancellationToken cancellationToken = default)
    {
        if (output is null)
        {
            throw new InvalidOperationException(L.T("Máy này không phát được âm thanh."));
        }

        string name = NameOf(peer);
        var session = new Listening(peer, name, settings.Current.AudioLatency)
        {
            // Left at 0 last time: pressing "Nghe" means to hear it, so it starts at full rather than silent.
            Volume = settings.Current.ListenVolumes.GetValueOrDefault(peer.Value, 100) is > 0 and var level ? level : 100,
        };
        lock (gate)
        {
            if (listening is not null)
            {
                session.Dispose();
                throw new InvalidOperationException(L.T("Đang nghe âm thanh từ một máy khác."));
            }

            listening = session;
        }

        string? remembered = password is null ? passwords.Get(peer) : null;
        try
        {
            var body = new AudioListenBody(session.Id, Convert.ToBase64String(session.Key), session.Port, password ?? remembered);
            using HttpResponseMessage response = await client.SendAsync(
                peer, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, "audio/listeners")) { Content = JsonContent.Create(body) },
                RequestTimeout, cancellationToken);
            // A source too old to be controlled answers 204, without a body.
            session.CanControl = response.StatusCode == HttpStatusCode.OK
                && (await PeerClient.ReadAsync<AudioJoinResponse>(response, cancellationToken)).Control;
            await PeerClient.EnsureSuccessAsync(response, cancellationToken);
            session.Play(output, L.F("Đang nghe âm thanh từ {0}", name), () => _ = StopListeningAsync(),
                session.CanControl ? key => _ = PressFromOutsideAsync(key, name) : null, settings.Current.AudioOutputDevice);
        }
        catch (PeerApiException ex) when (remembered is not null && ex.Code is ApiErrorCodes.WrongPassword or ApiErrorCodes.PasswordRequired)
        {
            // The source changed or removed its password.
            passwords.Remove(peer);
            EndListening(session);
            throw;
        }
        catch
        {
            EndListening(session);
            throw;
        }

        if (password is not null)
        {
            passwords.Set(peer, password);
        }

        logger.LogInformation("Listening to {Peer}", peer);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Âm lượng" of the sound we listen to, in percent: this device's own level, remembered per source.</summary>
    public int ListenVolume
    {
        get
        {
            lock (gate)
            {
                return listening?.Volume ?? 100;
            }
        }
    }

    /// <summary>Sets <see cref="ListenVolume"/> at once (0 to <see cref="EffectiveSettings.MaxListenVolume"/>); nothing while not listening.</summary>
    public void SetListenVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, EffectiveSettings.MaxListenVolume);
        lock (gate)
        {
            if (listening is not { } session)
            {
                return;
            }

            session.Volume = percent;
            unsavedVolume = (session.PeerId, percent);
        }
    }

    /// <summary>"Điều khiển": presses <paramref name="key"/> on the device we listen to.</summary>
    /// <exception cref="InvalidOperationException">Not listening, or that device cannot be controlled.</exception>
    /// <exception cref="PeerUnreachableException">It could not be reached.</exception>
    /// <exception cref="PeerApiException">It refused (we no longer listen there).</exception>
    public async Task PressAsync(MediaKey key, CancellationToken cancellationToken = default)
    {
        Listening? session;
        lock (gate)
        {
            session = listening;
        }

        if (session is not { CanControl: true })
        {
            throw new InvalidOperationException(L.T("Máy đang phát không cho điều khiển."));
        }

        using HttpResponseMessage response = await client.SendAsync(
            session.PeerId,
            b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"audio/listeners/{session.Id}/keys"))
            {
                Content = JsonContent.Create(new AudioKeyBody(MediaKeyNames.Of(key))),
            },
            RequestTimeout,
            cancellationToken);
        await PeerClient.EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>A key from outside the app (the notification): a failure becomes a notice.</summary>
    private async Task PressFromOutsideAsync(MediaKey key, string name)
    {
        try
        {
            await PressAsync(key);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PeerUnreachableException or PeerApiException or HttpRequestException)
        {
            logger.LogInformation(ex, "Could not press {Key} on {Name}", key, name);
            Notice?.Invoke(this, L.F("Không điều khiển được {0}.", name));
        }
    }

    public async Task StopListeningAsync()
    {
        Listening? session;
        lock (gate)
        {
            session = listening;
        }

        if (session is not null && EndListening(session))
        {
            await LeaveAsync(session);
        }
    }

    internal void HandleEnded(DeviceId caller, string sessionId, string? reason)
    {
        Listening? session;
        lock (gate)
        {
            session = listening is { } current && current.PeerId == caller && current.Id == sessionId ? current : null;
        }

        if (session is not null && EndListening(session))
        {
            Notice?.Invoke(this, reason switch
            {
                AudioEndReasons.Removed => L.F("{0} không cho bạn nghe nữa.", session.Name),
                AudioEndReasons.Stopped => L.F("{0} đã ngừng phát cho bạn.", session.Name),
                _ => L.F("{0} đã tắt phát âm thanh.", session.Name),
            });
        }
    }

    /// <summary>The source we listen to now lets us press its media keys, or no longer.</summary>
    internal void HandleControl(DeviceId caller, string sessionId, bool allowed)
    {
        lock (gate)
        {
            if (listening is not { } current || current.PeerId != caller || current.Id != sessionId || current.CanControl == allowed)
            {
                return;
            }

            current.CanControl = allowed;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        timer.Dispose();
        SaveVolume();
        Broadcast? ended;
        Listening? session;
        lock (gate)
        {
            (ended, broadcast) = (broadcast, null);
            (session, listening) = (listening, null);
        }

        ended?.Dispose();
        session?.Dispose();
    }

    private void OnTick()
    {
        SaveVolume();
        RemoveListener(l => l.SilentFor > ListenerGone);

        Listening? session;
        lock (gate)
        {
            session = listening;
        }

        if (session is null)
        {
            return;
        }

        session.SendKeepAlive();
        (AudioStreamStats stats, AudioListeningStats shown) = session.Measure();
        StatsChanged?.Invoke(this, shown);
        if (stats.Asked + stats.Stretched + stats.Concealed + stats.Skipped > 0)
        {
            logger.LogInformation(
                "Audio from {Name}: {Received} received, {Asked} asked again, {Recovered} from copies, {Late} late, {Stretched} waited, {Concealed} concealed, {Skipped} skipped, {Depth}/{Target} frames in hand",
                session.Name, stats.Received, stats.Asked, stats.Recovered, stats.Late, stats.Stretched, stats.Concealed, stats.Skipped, stats.Depth, stats.TargetFrames);
        }

        if (session.SilentFor > SourceGone && EndListening(session))
        {
            Notice?.Invoke(this, L.F("Mất tín hiệu âm thanh từ {0}.", session.Name));
            _ = LeaveAsync(session);
        }
    }

    /// <returns>Whether any listener was removed.</returns>
    private bool RemoveListener(Func<Listener, bool> match)
    {
        Broadcast? idle = null;
        bool removed;
        lock (gate)
        {
            removed = broadcast?.RemoveAll(match) > 0;
            if (broadcast is { Listeners.Count: 0 })
            {
                // Nobody listens: stop capturing until someone does.
                (idle, broadcast) = (broadcast, null);
            }
        }

        idle?.Dispose();
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    private void SaveVolume()
    {
        (DeviceId Source, int Percent)? pending;
        lock (gate)
        {
            (pending, unsavedVolume) = (unsavedVolume, null);
        }

        if (pending is not { } volume)
        {
            return;
        }

        settings.Update(s =>
        {
            var volumes = new Dictionary<string, int>(s.ListenVolumes ?? new(), StringComparer.Ordinal);
            if (volume.Percent == 100)
            {
                volumes.Remove(volume.Source.Value);
            }
            else
            {
                volumes[volume.Source.Value] = volume.Percent;
            }

            return s with { ListenVolumes = volumes.Count == 0 ? null : volumes };
        });
    }

    /// <returns>False when the session had already ended.</returns>
    private bool EndListening(Listening session)
    {
        lock (gate)
        {
            if (listening != session)
            {
                return false;
            }

            listening = null;
        }

        session.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task LeaveAsync(Listening session)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                session.PeerId, b => new HttpRequestMessage(HttpMethod.Delete, new Uri(b, $"audio/listeners/{session.Id}")),
                RequestTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is PeerUnreachableException or HttpRequestException or OperationCanceledException)
        {
            // It stops sending by itself once our keep-alives stop.
            logger.LogDebug(ex, "Could not tell {Peer} we stopped listening", session.PeerId);
        }
    }

    private async Task TellEndedAsync(AudioListener listener, string? reason = null)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                listener.PeerId,
                b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"audio/listening/{listener.SessionId}/ended"))
                {
                    Content = reason is null ? null : JsonContent.Create(new AudioEndedBody(reason)),
                },
                RequestTimeout,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is PeerUnreachableException or HttpRequestException or OperationCanceledException)
        {
            // It notices the silence by itself.
            logger.LogDebug(ex, "Could not tell {Peer} the sound stopped", listener.PeerId);
        }
    }

    private async Task TellControlAsync(AudioListener listener, bool allowed)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                listener.PeerId,
                b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"audio/listening/{listener.SessionId}/control"))
                {
                    Content = JsonContent.Create(new AudioControlBody(allowed)),
                },
                RequestTimeout,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is PeerUnreachableException or HttpRequestException or OperationCanceledException)
        {
            // Its keys are refused anyway; it learns again when it listens next.
            logger.LogDebug(ex, "Could not tell {Peer} about control", listener.PeerId);
        }
    }

    private string NameOf(DeviceId peer) => trust.Get(peer)?.ShownName ?? peer.ShortForm;

    private static bool IsValidSessionId(string? id) => id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit);

    private static byte[]? Key(string? base64)
    {
        byte[] key = new byte[AudioCipher.KeySize];
        return base64 is not null && Convert.TryFromBase64String(base64, key, out int written) && written == AudioCipher.KeySize ? key : null;
    }
}
