using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Engine.Audio;

public sealed partial class AudioService
{
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
}
