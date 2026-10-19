using System.Diagnostics;
using Concentus;
using Concentus.Enums;

namespace Shorekeeper.Core.Audio;

/// <summary>One 10 ms frame of <see cref="AudioStreamFormat.FrameValues"/> interleaved samples; only valid during the call.</summary>
public delegate void AudioFrameHandler(ReadOnlySpan<float> frame);

/// <summary>Fills one 10 ms frame of <see cref="AudioStreamFormat.FrameValues"/> interleaved samples to play.</summary>
public delegate void AudioFrameReader(Span<short> pcm);

/// <summary>"Độ trễ âm thanh" on the listening side.</summary>
public enum AudioLatency
{
    /// <summary>30 ms in hand: for games.</summary>
    Low,

    /// <summary>80 ms in hand: for music, rarely a gap when Wi-Fi stalls.</summary>
    Stable,
}

/// <summary>The audio stream's format: 48 kHz stereo in 10 ms frames, over UDP.</summary>
public static class AudioStreamFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameMilliseconds = 10;
    public const int FrameSamples = SampleRate * FrameMilliseconds / 1000;

    /// <summary>Interleaved values in one frame.</summary>
    public const int FrameValues = FrameSamples * Channels;

    /// <summary>Frames the listener keeps in hand before playing.</summary>
    public static int BufferFrames(AudioLatency latency) => latency == AudioLatency.Stable ? 8 : 3;
}

/// <summary>Sending side: turns 10 ms frames into numbered <see cref="AudioPacket"/>s.</summary>
public sealed class AudioStreamEncoder
{
    // Constant bit rate: every packet the same size, whatever plays.
    private const int Bitrate = 128_000;

    private readonly IOpusEncoder encoder;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly byte[] previous = new byte[AudioPacket.MaxSize / 2];
    private readonly byte[] current = new byte[AudioPacket.MaxSize / 2];
    private int previousLength;
    private uint sequence;

    public AudioStreamEncoder()
    {
        // Restricted low delay: CELT only, the shortest look-ahead Opus has (2.5 ms).
        encoder = OpusCodecFactory.CreateEncoder(AudioStreamFormat.SampleRate, AudioStreamFormat.Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = Bitrate;
        encoder.UseVBR = false;
    }

    /// <summary>Encodes one frame of <see cref="AudioStreamFormat.FrameValues"/> interleaved samples into <paramref name="packet"/>.</summary>
    /// <returns>The packet's length.</returns>
    public int Encode(ReadOnlySpan<float> frame, Span<byte> packet)
    {
        int length = encoder.Encode(frame, AudioStreamFormat.FrameSamples, current, current.Length);
        int size = new AudioPacket(sequence++, clock.Elapsed.Ticks / 10, current.AsSpan(0, length), previous.AsSpan(0, previousLength)).Write(packet);
        current.AsSpan(0, length).CopyTo(previous);
        previousLength = length;
        return size;
    }
}
