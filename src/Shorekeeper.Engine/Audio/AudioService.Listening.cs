using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Engine.Audio;

public sealed partial class AudioService
{
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
        private int volume = 100;

        // Only on the output's thread: the level the last frame ended at, for the next one to glide from.
        private int playedVolume = -1;

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

        /// <summary>In percent; read for each frame on the output's thread.</summary>
        public int Volume
        {
            get => Volatile.Read(ref volume);
            set => Volatile.Write(ref volume, value);
        }

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

        /// <summary>The source said we may press its media keys.</summary>
        public volatile bool CanControl;

        public void Play(IAudioOutput output, string title, Action stopRequested, Action<MediaKey>? keyRequested, string? device)
        {
            Interlocked.Exchange(ref lastHeard, Stopwatch.GetTimestamp());
            playing = output.Start(Read, title, stopRequested, keyRequested, device);
        }

        private void Read(Span<short> pcm)
        {
            decoder.Read(pcm);
            int to = Volume;
            // The first frame starts at the remembered level rather than gliding up to it.
            AudioGain.Apply(pcm, playedVolume < 0 ? to : playedVolume, to);
            playedVolume = to;
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
