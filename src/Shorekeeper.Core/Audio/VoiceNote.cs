using Concentus;
using Concentus.Enums;

namespace Shorekeeper.Core.Audio;

/// <summary>A recorded voice message (docs/15-chat.md §3): the Ogg Opus file, how long it plays, and its waveform.</summary>
/// <param name="Wave">The peak of each of <see cref="VoiceRecorder.WaveBars"/> slices, 0 to 255, loudest at 255.</param>
public sealed record VoiceNote(byte[] Ogg, int DurationMs, byte[] Wave);

/// <summary>
/// Records a voice message from the microphone's 10 ms frames (48 kHz stereo, as <see cref="AudioFrameHandler"/>
/// delivers them): mono Opus for speech at 24 kbps in 20 ms packets, about 3 KB a second. Frames come on the capture
/// thread; <see cref="Level"/> and <see cref="DurationMs"/> may be read from another.
/// </summary>
public sealed class VoiceRecorder
{
    public const int MaxDurationMs = 5 * 60 * 1000;
    public const int WaveBars = 48;

    private const int Bitrate = 24_000;
    private const int PacketSamples = AudioStreamFormat.SampleRate / 50;

    private readonly IOpusEncoder encoder;
    private readonly float[] pending = new float[PacketSamples];
    private readonly byte[] buffer = new byte[1275];
    private readonly List<byte[]> packets = [];
    private readonly List<float> peaks = [];
    private int filled;
    private float packetPeak;
    private int count;

    public VoiceRecorder()
    {
        encoder = OpusCodecFactory.CreateEncoder(AudioStreamFormat.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = Bitrate;
    }

    /// <summary>How loud the last frame was, 0 to 1: the meter while recording.</summary>
    public float Level { get; private set; }

    public int DurationMs => Volatile.Read(ref count) * 20;

    /// <summary>The longest a voice message may be has been recorded; later frames are dropped.</summary>
    public bool IsFull => DurationMs >= MaxDurationMs;

    /// <summary>One 10 ms frame of <see cref="AudioStreamFormat.FrameValues"/> interleaved stereo samples.</summary>
    public void Add(ReadOnlySpan<float> frame)
    {
        if (IsFull)
        {
            return;
        }

        float peak = 0;
        for (int i = 0; i < AudioStreamFormat.FrameSamples; i++)
        {
            float mono = (frame[2 * i] + frame[(2 * i) + 1]) / 2;
            pending[filled++] = mono;
            peak = Math.Max(peak, Math.Abs(mono));
        }

        Level = Math.Min(1, peak);
        packetPeak = Math.Max(packetPeak, peak);
        if (filled < PacketSamples)
        {
            return;
        }

        int length = encoder.Encode(pending, PacketSamples, buffer, buffer.Length);
        packets.Add(buffer.AsSpan(0, length).ToArray());
        peaks.Add(packetPeak);
        Volatile.Write(ref count, packets.Count);
        filled = 0;
        packetPeak = 0;
    }

    /// <summary>Call once the microphone has stopped.</summary>
    public VoiceNote Finish() =>
        new(OggOpus.Write(new OpusClip(1, encoder.Lookahead, packets), PacketSamples), DurationMs, Wave());

    private byte[] Wave()
    {
        byte[] bars = new byte[WaveBars];
        float loudest = peaks.Count == 0 ? 0 : peaks.Max();
        if (loudest < 0.01f)
        {
            return bars;
        }

        for (int bar = 0; bar < WaveBars; bar++)
        {
            int from = bar * peaks.Count / WaveBars;
            int to = Math.Max(from + 1, (bar + 1) * peaks.Count / WaveBars);
            float peak = from < peaks.Count ? peaks.Skip(from).Take(to - from).Max() : 0;
            bars[bar] = (byte)Math.Round(255 * peak / loudest);
        }

        return bars;
    }
}

/// <summary>
/// Plays a voice message: decodes it a packet at a time into the 10 ms stereo frames an output asks for, then
/// silence once it has ended. Frames are asked for on the output's thread; <see cref="PositionMs"/> and
/// <see cref="HasEnded"/> may be read from another.
/// </summary>
public sealed class VoiceNotePlayer
{
    private readonly OpusClip clip;
    private readonly IOpusDecoder decoder;
    private readonly float[] decoded = new float[5760 * 2];
    private int decodedCount;
    private int decodedAt;
    private int next;
    private int skip;
    private long played;

    private VoiceNotePlayer(OpusClip clip)
    {
        this.clip = clip;
        decoder = OpusCodecFactory.CreateDecoder(AudioStreamFormat.SampleRate, clip.Channels);
        skip = clip.PreSkip;
    }

    /// <returns>Null when <paramref name="ogg"/> is not a voice message that can be played.</returns>
    public static VoiceNotePlayer? Open(byte[] ogg) => OggOpus.Read(ogg) is { } clip ? new VoiceNotePlayer(clip) : null;

    public int PositionMs => (int)(Volatile.Read(ref played) / (AudioStreamFormat.SampleRate / 1000));

    public bool HasEnded { get; private set; }

    /// <summary>Fills one 10 ms frame of interleaved stereo samples (<see cref="AudioFrameReader"/>).</summary>
    public void Read(Span<short> pcm)
    {
        int frames = pcm.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            float value = 0;
            if (NextSample(out float sample))
            {
                value = sample;
                Volatile.Write(ref played, played + 1);
            }

            short s = (short)Math.Clamp(value * 32767f, short.MinValue, short.MaxValue);
            pcm[2 * i] = s;
            pcm[(2 * i) + 1] = s;
        }
    }

    /// <summary>The next mono sample (stereo clips are mixed down), past the encoder's look-ahead.</summary>
    private bool NextSample(out float sample)
    {
        while (true)
        {
            if (decodedAt < decodedCount)
            {
                int channels = clip.Channels;
                sample = channels == 1 ? decoded[decodedAt] : (decoded[decodedAt * 2] + decoded[(decodedAt * 2) + 1]) / 2;
                decodedAt++;
                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                return true;
            }

            if (next >= clip.Packets.Count)
            {
                HasEnded = true;
                sample = 0;
                return false;
            }

            decodedCount = decoder.Decode(clip.Packets[next++], decoded, decoded.Length / clip.Channels, false);
            decodedAt = 0;
        }
    }
}
