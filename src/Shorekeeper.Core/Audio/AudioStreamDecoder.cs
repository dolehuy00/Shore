using Concentus;

namespace Shorekeeper.Core.Audio;

/// <summary>What the stream did during one reporting window.</summary>
/// <param name="Recovered">Frames lost on the way but played from the copy in the next packet.</param>
/// <param name="Concealed">Frames lost: newer ones had arrived, so Opus filled this one in with a guess (or silence after a few).</param>
/// <param name="Stretched">Turns with nothing newer at all: the stream ran late, so a guess played while it waited for it.</param>
/// <param name="Late">Packets that arrived after their turn had passed.</param>
/// <param name="Skipped">Frames dropped to bring the delay back to the target after a burst.</param>
/// <param name="OverMs">Packets later than 20, 40, 60 and 100 ms behind the window's fastest: what each buffer size would have missed.</param>
public sealed record AudioStreamStats(
    int Received, int Played, int Recovered, int Concealed, int Stretched, int Late, int Skipped, int Depth, int TargetFrames,
    double DelayP50Ms, double DelayP99Ms, double DelayMaxMs, int[] OverMs);

/// <summary>
/// Receiving side: a jitter buffer that holds a target number of 10 ms frames before playing, then hands out one frame
/// per call. A lost frame is taken from the next packet's copy, else concealed. When the stream runs late instead (the
/// phone's Wi-Fi scanning stalls it for a moment), the buffer waits for it, playing a guess meanwhile, and raises the
/// target to ride out the next stall; quiet seconds bring it back down to the base.
/// </summary>
public sealed class AudioStreamDecoder
{
    private const int Slots = 128;
    // A burst after a stall may queue more than the target; beyond this many extra frames the oldest are dropped.
    private const int Slack = 3;
    // Opus' guesses only sound right for a few frames; after that, silence.
    private const int ConcealFrames = 5;
    // Nothing for this many frames: the sender stopped. Wait for the target to fill again.
    private const int StopAfterMisses = 50;
    // How often the queue's shortest length is checked against the target (1 s).
    private const int CreepWindow = 100;
    // The most a stall may raise the target to (120 ms), and how long without one before it steps back down (2 s).
    private const int MaxFrames = 12;
    private const int DecayAfterReads = 200;
    private static readonly int[] Thresholds = [20, 40, 60, 100];

    private readonly Lock gate = new();
    private readonly Slot[] slots = new Slot[Slots];
    private readonly IOpusDecoder decoder;
    private readonly int baseFrames;
    private int targetFrames;
    private int lateRun;
    private int quietReads;
    private readonly List<long> offsets = [];
    private bool playing;
    private bool anyPacket;
    private uint highest;
    private uint next;
    private int missesInRow;
    private int creepReads;
    private int creepMinDepth = int.MaxValue;
    private int received;
    private int played;
    private int recovered;
    private int concealed;
    private int stretched;
    private int late;
    private int skipped;

    public AudioStreamDecoder(int targetFrames)
    {
        baseFrames = this.targetFrames = Math.Clamp(targetFrames, 1, MaxFrames);
        decoder = OpusCodecFactory.CreateDecoder(AudioStreamFormat.SampleRate, AudioStreamFormat.Channels);
        for (int i = 0; i < Slots; i++)
        {
            slots[i] = new Slot();
        }
    }

    /// <param name="arrivalMicros">On the receiver's clock.</param>
    public void Add(ReadOnlySpan<byte> datagram, long arrivalMicros)
    {
        if (!AudioPacket.TryRead(datagram, out AudioPacket packet))
        {
            return;
        }

        lock (gate)
        {
            received++;
            offsets.Add(arrivalMicros - packet.SentMicros);
            if (!anyPacket || (int)(packet.Sequence - highest) > 0)
            {
                highest = packet.Sequence;
                anyPacket = true;
            }

            if (playing && (int)(packet.Sequence - next) < 0)
            {
                late++;
                return;
            }

            slots[packet.Sequence % Slots].Set(packet);
        }
    }

    /// <summary>Fills <paramref name="pcm"/> (<see cref="AudioStreamFormat.FrameValues"/> values) with the next frame, or silence.</summary>
    public void Read(Span<short> pcm)
    {
        lock (gate)
        {
            if (!playing)
            {
                if (!anyPacket || CountFrom(highest - (uint)targetFrames + 1) < targetFrames)
                {
                    pcm.Clear();
                    return;
                }

                playing = true;
                next = highest - (uint)targetFrames + 1;
            }

            int depth = (int)(highest - next) + 1;
            if (depth > targetFrames + Slack)
            {
                int drop = depth - targetFrames;
                for (int i = 0; i < drop; i++)
                {
                    slots[(next + (uint)i) % Slots].Clear();
                }

                next += (uint)drop;
                skipped += drop;
                depth -= drop;
            }

            // After a stall the queue may stay a few frames longer than the target, too few to trim above, adding delay
            // for good. When it never got down to the target for a whole second, one frame goes.
            creepMinDepth = Math.Min(creepMinDepth, depth);
            if (++creepReads == CreepWindow)
            {
                if (creepMinDepth > targetFrames)
                {
                    slots[next % Slots].Clear();
                    next++;
                    skipped++;
                }

                creepReads = 0;
                creepMinDepth = int.MaxValue;
            }

            if (++quietReads >= DecayAfterReads && targetFrames > baseFrames)
            {
                targetFrames--;
                quietReads = 0;
            }

            Slot slot = slots[next % Slots];
            Slot after = slots[(next + 1) % Slots];
            if (slot.Holds(next))
            {
                Decode(slot.Frame.AsSpan(0, slot.FrameLength), pcm);
                played++;
                missesInRow = lateRun = 0;
            }
            else if (after.Holds(next + 1) && after.PreviousLength > 0)
            {
                Decode(after.Previous.AsSpan(0, after.PreviousLength), pcm);
                recovered++;
                missesInRow = lateRun = 0;
            }
            else if ((int)(highest - next) < 0)
            {
                // Nothing newer has arrived: the stream is late, not lost. Wait for this frame rather than skip it,
                // and keep that much more in hand from now on.
                stretched++;
                lateRun++;
                quietReads = 0;
                targetFrames = Math.Min(MaxFrames, Math.Max(targetFrames, baseFrames + lateRun));
                Miss(pcm);
                return;
            }
            else
            {
                concealed++;
                Miss(pcm);
            }

            slot.Clear();
            next++;
        }
    }

    private void Miss(Span<short> pcm)
    {
        missesInRow++;
        if (missesInRow <= ConcealFrames)
        {
            Decode([], pcm);
        }
        else
        {
            pcm.Clear();
        }

        if (missesInRow >= StopAfterMisses)
        {
            playing = false;
            missesInRow = lateRun = 0;
            targetFrames = baseFrames;
        }
    }

    public AudioStreamStats TakeStats()
    {
        lock (gate)
        {
            // Delays against the window's fastest packet: the two clocks drift apart too slowly to matter within one window.
            long[] sorted = [.. offsets];
            Array.Sort(sorted);
            long fastest = sorted.Length > 0 ? sorted[0] : 0;
            double Ms(long offset) => (offset - fastest) / 1000.0;
            double At(double q) => sorted.Length > 0 ? Ms(sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * q))]) : 0;
            int[] over = [.. Thresholds.Select(t => sorted.Count(o => Ms(o) > t))];
            var stats = new AudioStreamStats(
                received, played, recovered, concealed, stretched, late, skipped, playing ? (int)(highest - next) + 1 : 0, targetFrames,
                At(0.5), At(0.99), sorted.Length > 0 ? Ms(sorted[^1]) : 0, over);
            offsets.Clear();
            received = played = recovered = concealed = stretched = late = skipped = 0;
            return stats;
        }
    }

    private int CountFrom(uint first)
    {
        int count = 0;
        for (uint s = first; (int)(s - highest) <= 0; s++)
        {
            count += slots[s % Slots].Holds(s) ? 1 : 0;
        }

        return count;
    }

    private void Decode(ReadOnlySpan<byte> frame, Span<short> pcm)
    {
        int samples = decoder.Decode(frame, pcm, AudioStreamFormat.FrameSamples);
        if (samples < AudioStreamFormat.FrameSamples)
        {
            pcm[(samples * AudioStreamFormat.Channels)..].Clear();
        }
    }

    private sealed class Slot
    {
        public uint Sequence { get; private set; }

        public bool Filled { get; private set; }

        public byte[] Frame { get; } = new byte[AudioPacket.MaxSize];

        public int FrameLength { get; private set; }

        public byte[] Previous { get; } = new byte[AudioPacket.MaxSize];

        public int PreviousLength { get; private set; }

        public bool Holds(uint sequence) => Filled && Sequence == sequence;

        public void Set(AudioPacket packet)
        {
            Sequence = packet.Sequence;
            packet.Frame.CopyTo(Frame);
            FrameLength = packet.Frame.Length;
            packet.Previous.CopyTo(Previous);
            PreviousLength = packet.Previous.Length;
            Filled = true;
        }

        public void Clear() => Filled = false;
    }
}
