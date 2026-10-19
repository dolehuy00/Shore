using Concentus;

namespace Shorekeeper.Core.Audio;

/// <summary>What the stream did during one reporting window.</summary>
/// <param name="Recovered">Frames lost on the way but played from the copy in the next packet.</param>
/// <param name="Concealed">Frames lost: newer ones had arrived, so Opus filled this one in with a guess (or silence after a few).</param>
/// <param name="Stretched">Turns with nothing newer at all: the stream ran late, so a guess played while it waited for it.</param>
/// <param name="Late">Packets that arrived after their turn had passed.</param>
/// <param name="Skipped">Frames dropped to bring the delay back to the target after a burst.</param>
/// <param name="Asked">Frames asked of the source again (<see cref="AudioStreamDecoder.TakeMissing"/>).</param>
/// <param name="OverMs">Packets later than 20, 40, 60 and 100 ms behind the window's fastest: what each buffer size would have missed.</param>
public sealed record AudioStreamStats(
    int Received, int Played, int Recovered, int Concealed, int Stretched, int Late, int Skipped, int Asked, int Depth, int TargetFrames,
    double DelayP50Ms, double DelayP99Ms, double DelayMaxMs, int[] OverMs);

/// <summary>
/// Receiving side: a jitter buffer that holds a target number of 10 ms frames before playing, then hands out one frame
/// per call. A lost frame is taken from the next packet's copy, else concealed. When the stream runs late instead (the
/// phone's Wi-Fi scanning stalls it for a moment), the buffer waits for it, playing a guess meanwhile, and raises the
/// target to ride out the next stall; quiet seconds bring it back down to the base.
/// <para>
/// In <see cref="AudioLatency.Preserve"/> nothing audible is given up for delay: a missing frame is waited for while the
/// source resends it (<see cref="TakeMissing"/>), a stall or a loss raises the target as far as 3 s, and extra delay is
/// only shed by dropping silent frames.
/// </para>
/// </summary>
public sealed class AudioStreamDecoder
{
    // Room for the most a preserving buffer holds, and then some.
    private const int Slots = 512;
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
    // Preserving: up to 3 s; after 10 s without trouble the target steps down one frame every half second. A frame that
    // never came is waited for up to 300 ms for its resend; one that never comes at all adds 50 ms. Asking again for a
    // frame waits 30 ms. A frame quieter than about -66 dB may be dropped to shed delay, a few per turn at most.
    private const int PreserveMaxFrames = 300;
    private const int PreserveDecayAfterReads = 1000;
    private const int PreserveDecayEveryReads = 50;
    private const int PreserveWaitFrames = 30;
    private const int PreserveLossFrames = 5;
    private const long ResendRetryMicros = 30_000;
    private const int SilenceLevel = 16;
    private const int MaxTrimsPerRead = 4;
    private static readonly int[] Thresholds = [20, 40, 60, 100];

    private readonly Lock gate = new();
    private readonly Slot[] slots = new Slot[Slots];
    private readonly IOpusDecoder decoder;
    private readonly int baseFrames;
    private readonly bool preserve;
    private readonly int maxFrames;
    private int targetFrames;
    private int lateRun;
    private int lostWait;
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
    private int asked;

    /// <param name="preserve"><see cref="AudioLatency.Preserve"/>: wait rather than drop anything audible.</param>
    public AudioStreamDecoder(int targetFrames, bool preserve = false)
    {
        this.preserve = preserve;
        maxFrames = preserve ? PreserveMaxFrames : MaxFrames;
        baseFrames = this.targetFrames = Math.Clamp(targetFrames, 1, maxFrames);
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
            if (depth > (preserve ? maxFrames : targetFrames + Slack))
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
                if (creepMinDepth > targetFrames && !preserve)
                {
                    slots[next % Slots].Clear();
                    next++;
                    skipped++;
                }

                creepReads = 0;
                creepMinDepth = int.MaxValue;
            }

            if (++quietReads >= (preserve ? PreserveDecayAfterReads : DecayAfterReads) && targetFrames > baseFrames)
            {
                targetFrames--;
                quietReads = preserve ? PreserveDecayAfterReads - PreserveDecayEveryReads : 0;
            }

            bool decoded = preserve && depth > targetFrames && TrimSilence(pcm, depth);
            Slot slot = slots[next % Slots];
            Slot after = slots[(next + 1) % Slots];
            if (slot.Holds(next))
            {
                if (!decoded)
                {
                    Decode(slot.Frame.AsSpan(0, slot.FrameLength), pcm);
                }

                played++;
                missesInRow = lateRun = 0;
            }
            else if (after.Holds(next + 1) && after.PreviousLength > 0)
            {
                Decode(after.Previous.AsSpan(0, after.PreviousLength), pcm);
                recovered++;
                missesInRow = lateRun = 0;
            }
            else if ((int)(highest - next) < 0 || (preserve && lostWait++ < PreserveWaitFrames))
            {
                // Nothing newer has arrived: the stream is late, not lost. Wait for this frame rather than skip it,
                // and keep that much more in hand from now on. Preserving, a frame lost on the way is waited for too,
                // while the source sends it again.
                stretched++;
                lateRun++;
                quietReads = 0;
                targetFrames = Math.Min(maxFrames, Math.Max(targetFrames, baseFrames + lateRun));
                Miss(pcm);
                return;
            }
            else
            {
                concealed++;
                if (preserve)
                {
                    // Gone for good: keep more in hand so the next loss has longer to come back.
                    quietReads = 0;
                    targetFrames = Math.Min(maxFrames, targetFrames + PreserveLossFrames);
                }

                Miss(pcm);
            }

            slot.Clear();
            next++;
            lostWait = 0;
        }
    }

    /// <summary>
    /// Frames still missing although newer ones arrived (and no copy of them did), not asked for in the last 30 ms: the
    /// source may still have them. Called after each arrival; each one returned counts as asked for.
    /// </summary>
    /// <param name="nowMicros">On the receiver's clock, as given to <see cref="Add"/>.</param>
    /// <returns>How many were written to <paramref name="sequences"/>.</returns>
    public int TakeMissing(Span<uint> sequences, long nowMicros)
    {
        lock (gate)
        {
            if (!playing)
            {
                return 0;
            }

            int count = 0;
            int span = Math.Min((int)(highest - next), Slots - 1);
            for (uint s = next; (int)(s - next) < span && count < sequences.Length; s++)
            {
                Slot slot = slots[s % Slots];
                Slot after = slots[(s + 1) % Slots];
                if (slot.Holds(s) || (after.Holds(s + 1) && after.PreviousLength > 0) || slot.WasAskedFor(s, nowMicros))
                {
                    continue;
                }

                slot.AskFor(s, nowMicros);
                sequences[count++] = s;
            }

            asked += count;
            return count;
        }
    }

    /// <summary>Drops silent frames at the front while more than the target is queued.</summary>
    /// <returns>Whether <paramref name="pcm"/> already holds frame <c>next</c>, decoded while looking.</returns>
    private bool TrimSilence(Span<short> pcm, int depth)
    {
        for (int i = 0; i < MaxTrimsPerRead && depth > targetFrames; i++)
        {
            Slot slot = slots[next % Slots];
            if (!slot.Holds(next))
            {
                return false;
            }

            Decode(slot.Frame.AsSpan(0, slot.FrameLength), pcm);
            foreach (short value in pcm)
            {
                if (Math.Abs((int)value) > SilenceLevel)
                {
                    return true;
                }
            }

            slot.Clear();
            next++;
            skipped++;
            depth--;
        }

        return false;
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

        // Preserving, a long stall is waited out rather than started over (which would skip what it held up).
        if (missesInRow >= StopAfterMisses && !preserve)
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
                received, played, recovered, concealed, stretched, late, skipped, asked, playing ? (int)(highest - next) + 1 : 0, targetFrames,
                At(0.5), At(0.99), sorted.Length > 0 ? Ms(sorted[^1]) : 0, over);
            offsets.Clear();
            received = played = recovered = concealed = stretched = late = skipped = asked = 0;
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

        public bool WasAskedFor(uint sequence, long nowMicros) =>
            Asked && AskedSequence == sequence && nowMicros - AskedMicros < ResendRetryMicros;

        public void AskFor(uint sequence, long nowMicros)
        {
            Asked = true;
            AskedSequence = sequence;
            AskedMicros = nowMicros;
        }

        private bool Asked { get; set; }

        private uint AskedSequence { get; set; }

        private long AskedMicros { get; set; }

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
