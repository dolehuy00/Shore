using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
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
}

/// <summary>
/// "Phát âm thanh" (docs/05-protocol.md §4.7). A computer or phone turns it on; contacts that want to listen ask to join, each
/// with a fresh key and a UDP port of its own, and the source's password when it has one. The sound is captured only while someone listens, encoded once and sealed
/// for each listener. Listeners prove they are still there every second; either side stops after a few silent seconds.
/// A listener that misses packets asks for them again, and the source resends what it still has from the last 3 s.
/// </summary>
public sealed class AudioService : IDisposable
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
    private readonly Lock gate = new();
    private readonly ITimer timer;
    private readonly Dictionary<DeviceId, (int Count, long Since)> wrongPasswords = [];
    private Broadcast? broadcast;
    private Listening? listening;

    public AudioService(
        PeerClient client,
        TrustStore trust,
        LocalPresence presence,
        SettingsService settings,
        AudioPasswords passwords,
        TimeProvider timeProvider,
        ILogger<AudioService> logger,
        IAudioCapture? capture = null,
        IAudioOutput? output = null)
    {
        this.client = client;
        this.trust = trust;
        this.presence = presence;
        this.settings = settings;
        this.passwords = passwords;
        this.logger = logger;
        this.capture = capture;
        this.output = output;
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
        var session = new Listening(peer, name, settings.Current.AudioLatency);
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
            await PeerClient.EnsureSuccessAsync(response, cancellationToken);
            session.Play(output, L.F("Đang nghe âm thanh từ {0}", name), () => _ = StopListeningAsync());
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

    internal void HandleEnded(DeviceId caller, string sessionId)
    {
        Listening? session;
        lock (gate)
        {
            session = listening is { } current && current.PeerId == caller && current.Id == sessionId ? current : null;
        }

        if (session is not null && EndListening(session))
        {
            Notice?.Invoke(this, L.F("{0} đã tắt phát âm thanh.", session.Name));
        }
    }

    public void Dispose()
    {
        timer.Dispose();
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

    private async Task TellEndedAsync(AudioListener listener)
    {
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                listener.PeerId, b => new HttpRequestMessage(HttpMethod.Post, new Uri(b, $"audio/listening/{listener.SessionId}/ended")),
                RequestTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is PeerUnreachableException or HttpRequestException or OperationCanceledException)
        {
            // It notices the silence by itself.
            logger.LogDebug(ex, "Could not tell {Peer} the sound stopped", listener.PeerId);
        }
    }

    private string NameOf(DeviceId peer) => trust.Get(peer)?.ShownName ?? peer.ShortForm;

    private static bool IsValidSessionId(string? id) => id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit);

    private static byte[]? Key(string? base64)
    {
        byte[] key = new byte[AudioCipher.KeySize];
        return base64 is not null && Convert.TryFromBase64String(base64, key, out int written) && written == AudioCipher.KeySize ? key : null;
    }

    /// <summary>One listener of our sound, sealed with its own key.</summary>
    private sealed class Listener(AudioListener info, IPEndPoint endpoint, byte[] key) : IDisposable
    {
        // The last 3 s of what it was sent, sealed, for its resend requests.
        private const int HistoryPackets = 300;

        private readonly Lock historyGate = new();
        private readonly byte[][] history = new byte[HistoryPackets][];
        private readonly int[] historyLengths = new int[HistoryPackets];
        private readonly uint[] historySequences = new uint[HistoryPackets];
        private long lastHeard = Stopwatch.GetTimestamp();

        public AudioListener Info { get; } = info;

        public IPEndPoint Endpoint { get; } = endpoint;

        /// <summary>Used on the capture thread only.</summary>
        public AudioCipher Sealer { get; } = new(key);

        /// <summary>Used on the receive thread only.</summary>
        public AudioCipher KeepAlives { get; } = new(key);

        public TimeSpan SilentFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastHeard));

        public void Heard() => Interlocked.Exchange(ref lastHeard, Stopwatch.GetTimestamp());

        /// <summary>Remembers a sealed packet (its sequence number is in the readable header).</summary>
        public void Keep(ReadOnlySpan<byte> sealedPacket)
        {
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(sealedPacket[1..]);
            int index = (int)(sequence % HistoryPackets);
            lock (historyGate)
            {
                byte[] kept = history[index] ??= new byte[AudioPacket.MaxSize + AudioCipher.TagSize];
                sealedPacket.CopyTo(kept);
                historyLengths[index] = sealedPacket.Length;
                historySequences[index] = sequence;
            }
        }

        /// <summary>Copies packet <paramref name="sequence"/> as it was sent, when it is still kept.</summary>
        /// <returns>Its length, or 0.</returns>
        public int Recall(uint sequence, Span<byte> destination)
        {
            int index = (int)(sequence % HistoryPackets);
            lock (historyGate)
            {
                if (history[index] is not { } kept || historySequences[index] != sequence)
                {
                    return 0;
                }

                kept.AsSpan(0, historyLengths[index]).CopyTo(destination);
                return historyLengths[index];
            }
        }

        public void Dispose()
        {
            Sealer.Dispose();
            KeepAlives.Dispose();
        }
    }

    /// <summary>Our sound going out while anyone listens: one capture, one encoder, one socket for every listener.</summary>
    private sealed class Broadcast : IDisposable
    {
        private readonly ILogger logger;
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly AudioStreamEncoder encoder = new();
        private readonly byte[] plain = new byte[AudioPacket.MaxSize];
        private readonly byte[] sealedPacket = new byte[AudioPacket.MaxSize + AudioCipher.TagSize];
        private readonly Thread receiver;
        private readonly IDisposable capturing;
        // Removed listeners: the capture thread may be sealing for one right now, so their keys live as long as this.
        private readonly List<Listener> retired = [];
        private volatile Listener[] snapshot = [];
        private volatile bool disposed;

        public Broadcast(IAudioCapture capture, ILogger logger)
        {
            this.logger = logger;
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            receiver = new Thread(ReceiveKeepAlives) { IsBackground = true, Name = "Audio keep-alives" };
            receiver.Start();
            try
            {
                capturing = capture.Start(Send);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>Only changed under the service's lock.</summary>
        public List<Listener> Listeners { get; } = [];

        public void Add(Listener listener)
        {
            Listeners.Add(listener);
            snapshot = [.. Listeners];
        }

        public void Remove(DeviceId peer) => RemoveAll(l => l.Info.PeerId == peer);

        public int RemoveAll(Func<Listener, bool> match)
        {
            Listener[] gone = [.. Listeners.Where(match)];
            if (gone.Length > 0)
            {
                Listeners.RemoveAll(gone.Contains);
                retired.AddRange(gone);
                snapshot = [.. Listeners];
            }

            return gone.Length;
        }

        public void Dispose()
        {
            disposed = true;
            capturing.Dispose();
            socket.Dispose();
            receiver.Join();
            foreach (Listener listener in Listeners.Concat(retired))
            {
                listener.Dispose();
            }
        }

        private void Send(ReadOnlySpan<float> frame)
        {
            int length = encoder.Encode(frame, plain);
            foreach (Listener listener in snapshot)
            {
                plain.AsSpan(0, length).CopyTo(sealedPacket);
                int size = listener.Sealer.Seal(sealedPacket, length);
                listener.Keep(sealedPacket.AsSpan(0, size));
                try
                {
                    socket.SendTo(sealedPacket.AsSpan(0, size), SocketFlags.None, listener.Endpoint);
                }
                catch (SocketException ex)
                {
                    // The listener's Wi-Fi dropped for a moment; it stays until its keep-alives stop.
                    logger.LogDebug("Audio to {Endpoint}: {Error}", listener.Endpoint, ex.SocketErrorCode);
                }
            }
        }

        /// <summary>Keep-alives, and resend requests: what is still kept goes out again as it was.</summary>
        private void ReceiveKeepAlives()
        {
            byte[] buffer = new byte[AudioCipher.MaxResendSize];
            byte[] reply = new byte[AudioCipher.KeepAliveSize];
            byte[] resent = new byte[AudioPacket.MaxSize + AudioCipher.TagSize];
            uint[] sequences = new uint[AudioCipher.MaxResendSequences];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (!disposed)
            {
                int size;
                try
                {
                    size = socket.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException) when (!disposed)
                {
                    // Windows reports an earlier send to a closed port here; keep listening.
                    continue;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return;
                }

                foreach (Listener listener in snapshot)
                {
                    if (!listener.Endpoint.Equals(from))
                    {
                        continue;
                    }

                    if (listener.KeepAlives.OpenKeepAlive(buffer.AsSpan(0, size)))
                    {
                        listener.Heard();
                        Reply(listener, BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(1)), reply);
                    }
                    else if (listener.KeepAlives.OpenResendRequest(buffer.AsSpan(0, size), sequences) is var count and > 0)
                    {
                        listener.Heard();
                        Resend(listener, sequences.AsSpan(0, count), resent);
                    }
                }
            }
        }

        /// <summary>Answers a keep-alive, so the listener can time the round trip.</summary>
        private void Reply(Listener listener, uint counter, byte[] reply)
        {
            int size = listener.KeepAlives.SealKeepAliveReply(reply, counter);
            try
            {
                socket.SendTo(reply.AsSpan(0, size), SocketFlags.None, listener.Endpoint);
            }
            catch (SocketException ex)
            {
                logger.LogDebug("Audio keep-alive reply to {Endpoint}: {Error}", listener.Endpoint, ex.SocketErrorCode);
            }
        }

        private void Resend(Listener listener, ReadOnlySpan<uint> sequences, byte[] resent)
        {
            foreach (uint sequence in sequences)
            {
                int size = listener.Recall(sequence, resent);
                if (size == 0)
                {
                    continue;
                }

                try
                {
                    socket.SendTo(resent.AsSpan(0, size), SocketFlags.None, listener.Endpoint);
                }
                catch (SocketException ex)
                {
                    logger.LogDebug("Audio resend to {Endpoint}: {Error}", listener.Endpoint, ex.SocketErrorCode);
                }
            }
        }
    }

    /// <summary>Us listening to someone: a socket of our own, the jitter buffer and the device's output.</summary>
    private sealed class Listening : IDisposable
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly AudioCipher opener;
        private readonly AudioCipher keepAlives;
        private readonly AudioStreamDecoder decoder;
        private readonly Thread receiver;
        private readonly byte[] keepAlive = new byte[AudioCipher.KeepAliveSize];
        private IDisposable? playing;
        private IPEndPoint? source;
        private uint keepAliveCounter;
        private uint resendCounter;
        // When each of the last keep-alives went (Stopwatch timestamps, by counter), and the last round trip (TimeSpan ticks).
        private readonly long[] keepAliveSentAt = new long[16];
        private long pingTicks = -1;
        private long bytesReceived;
        // Only on the service's timer: the last 10 s of (sent, missed, gap frames), and the totals since listening started.
        private readonly Queue<(int Sent, int Missed, int Gap)> recent = [];
        private int lost;
        private int saved;
        private int gapFrames;
        private long lastHeard = Stopwatch.GetTimestamp();
        private volatile bool disposed;

        public Listening(DeviceId peer, string name, AudioLatency latency)
        {
            Latency = latency;
            PeerId = peer;
            Name = name;
            Key = AudioCipher.NewKey();
            opener = new AudioCipher(Key);
            keepAlives = new AudioCipher(Key);
            decoder = new AudioStreamDecoder(AudioStreamFormat.BufferFrames(latency), latency == AudioLatency.Preserve);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            receiver = new Thread(Receive) { IsBackground = true, Name = "Audio receive" };
            receiver.Start();
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public DeviceId PeerId { get; }

        public string Name { get; }

        public byte[] Key { get; }

        public int Port { get; }

        public AudioLatency Latency { get; }

        public TimeSpan SilentFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastHeard));

        /// <summary>Once a second: what the stream did in the last second, and how listening goes as shown.</summary>
        public (AudioStreamStats Last, AudioListeningStats Shown) Measure()
        {
            AudioStreamStats last = decoder.TakeStats();
            recent.Enqueue((last.Sent, last.Missed, last.Concealed + last.Stretched));
            if (recent.Count > 10)
            {
                recent.Dequeue();
            }

            int sent = recent.Sum(w => w.Sent);
            int missed = recent.Sum(w => w.Missed);
            lost += last.Missed;
            saved += last.Recovered + last.Resent;
            gapFrames += last.Concealed + last.Stretched;
            long ping = Interlocked.Read(ref pingTicks);
            var shown = new AudioListeningStats(
                Latency,
                AudioStreamFormat.BufferFrames(Latency) * AudioStreamFormat.FrameMilliseconds,
                ping < 0 ? null : TimeSpan.FromTicks(ping),
                last.Depth * AudioStreamFormat.FrameMilliseconds,
                last.TargetFrames * AudioStreamFormat.FrameMilliseconds,
                last.DelayP99Ms,
                sent > 0 ? 100.0 * missed / sent : 0,
                lost,
                Math.Min(saved, lost),
                gapFrames * AudioStreamFormat.FrameMilliseconds,
                recent.Sum(w => w.Gap) * AudioStreamFormat.FrameMilliseconds,
                (int)(Interlocked.Exchange(ref bytesReceived, 0) * 8 / 1000));
            return (last, shown);
        }

        public void Play(IAudioOutput output, string title, Action stopRequested)
        {
            Interlocked.Exchange(ref lastHeard, Stopwatch.GetTimestamp());
            playing = output.Start(decoder.Read, title, stopRequested);
        }

        /// <summary>Tells the source we still listen; also opens the way back through the source's firewall.</summary>
        public void SendKeepAlive()
        {
            if (Volatile.Read(ref source) is not { } to || disposed)
            {
                return;
            }

            uint counter = keepAliveCounter++;
            int size = keepAlives.SealKeepAlive(keepAlive, counter);
            Volatile.Write(ref keepAliveSentAt[counter % (uint)keepAliveSentAt.Length], Stopwatch.GetTimestamp());
            try
            {
                socket.SendTo(keepAlive.AsSpan(0, size), SocketFlags.None, to);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Silence takes care of it.
            }
        }

        public void Dispose()
        {
            disposed = true;
            playing?.Dispose();
            socket.Dispose();
            receiver.Join();
            opener.Dispose();
            keepAlives.Dispose();
        }

        private void Receive()
        {
            var clock = Stopwatch.StartNew();
            byte[] buffer = new byte[AudioPacket.MaxSize + AudioCipher.TagSize];
            byte[] request = new byte[AudioCipher.MaxResendSize];
            uint[] missing = new uint[AudioCipher.MaxResendSequences];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (!disposed)
            {
                int size;
                try
                {
                    size = socket.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException) when (!disposed)
                {
                    continue;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Add(ref bytesReceived, size);
                if (opener.OpenKeepAliveReply(buffer.AsSpan(0, size), out uint answered))
                {
                    long sentAt = Volatile.Read(ref keepAliveSentAt[answered % (uint)keepAliveSentAt.Length]);
                    Interlocked.Exchange(ref pingTicks, Stopwatch.GetElapsedTime(sentAt).Ticks);
                    continue;
                }

                int length = opener.Open(buffer.AsSpan(0, size));
                if (length < 0)
                {
                    continue;
                }

                long now = clock.Elapsed.Ticks / 10;
                decoder.Add(buffer.AsSpan(0, length), now);
                Interlocked.Exchange(ref lastHeard, Stopwatch.GetTimestamp());
                if (decoder.TakeMissing(missing, now) is var count and > 0)
                {
                    int requestSize = opener.SealResendRequest(request, resendCounter++, missing.AsSpan(0, count));
                    try
                    {
                        socket.SendTo(request.AsSpan(0, requestSize), SocketFlags.None, from);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        // Asked again on the next arrival.
                    }
                }

                if (!from.Equals(source))
                {
                    Volatile.Write(ref source, (IPEndPoint)from);
                    from = new IPEndPoint(IPAddress.Any, 0);
                }
            }
        }
    }
}
