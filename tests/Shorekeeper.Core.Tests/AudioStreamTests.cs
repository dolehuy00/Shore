using Shorekeeper.Core.Audio;

namespace Shorekeeper.Core.Tests;

public class AudioStreamTests
{
    [Fact]
    public void Packet_round_trips()
    {
        byte[] buffer = new byte[AudioPacket.MaxSize];
        int size = new AudioPacket(7, 123_456, [1, 2, 3], [4, 5]).Write(buffer);

        Assert.True(AudioPacket.TryRead(buffer.AsSpan(0, size), out AudioPacket packet));
        Assert.Equal(7u, packet.Sequence);
        Assert.Equal(123_456, packet.SentMicros);
        Assert.Equal([1, 2, 3], packet.Frame.ToArray());
        Assert.Equal([4, 5], packet.Previous.ToArray());
        Assert.False(AudioPacket.TryRead(buffer.AsSpan(0, size - 1), out _));
    }

    [Fact]
    public void Waits_for_the_target_then_plays_in_order()
    {
        var (decoder, packets) = Stream(frames: 8, targetFrames: 3);

        // One packet arrives and one frame plays every 10 ms: silence until three are queued, then every frame.
        for (int i = 0; i < 8; i++)
        {
            Tick(decoder, packets[i]);
        }

        AudioStreamStats stats = decoder.TakeStats();
        Assert.Equal(8, stats.Received);
        Assert.Equal(6, stats.Played);
        Assert.Equal(0, stats.Concealed + stats.Recovered + stats.Late + stats.Skipped);
        Assert.Equal(2, stats.Depth);
    }

    [Fact]
    public void A_lost_packet_is_played_from_the_next_ones_copy()
    {
        var (decoder, packets) = Stream(frames: 8, targetFrames: 3);

        for (int i = 0; i < 8; i++)
        {
            Tick(decoder, i == 4 ? null : packets[i]);
        }

        AudioStreamStats stats = decoder.TakeStats();
        Assert.Equal(1, stats.Recovered);
        Assert.Equal(0, stats.Concealed);
    }

    [Fact]
    public void A_late_stream_is_waited_for_and_the_target_raised()
    {
        var (decoder, packets) = Stream(frames: 10, targetFrames: 2);

        // Packets 3 to 6 are held up past their turn (a Wi-Fi scan), then arrive together: nothing is lost.
        for (int i = 0; i < 10; i++)
        {
            Tick(decoder, i is >= 3 and <= 6 ? null : packets[i]);
            if (i == 6)
            {
                Add(decoder, packets[3..7]);
            }
        }

        AudioStreamStats stats = decoder.TakeStats();
        Assert.Equal(0, stats.Late + stats.Concealed + stats.Skipped);
        Assert.True(stats.Stretched > 0);
        Assert.Equal(2 + stats.Stretched, stats.TargetFrames);
    }

    [Fact]
    public void A_raised_target_comes_back_down_after_quiet_seconds()
    {
        var (decoder, packets) = Stream(frames: 1000, targetFrames: 2);

        for (int i = 0; i < 1000; i++)
        {
            Tick(decoder, i is >= 10 and <= 13 ? null : packets[i]);
            if (i == 13)
            {
                Add(decoder, packets[10..14]);
            }
        }

        AudioStreamStats stats = decoder.TakeStats();
        Assert.Equal(2, stats.TargetFrames);
        Assert.True(stats.Depth <= 2);
        Assert.Equal(0, stats.Late + stats.Concealed);
    }

    [Fact]
    public void A_burst_beyond_the_slack_is_trimmed_back_to_the_target()
    {
        var (decoder, packets) = Stream(frames: 12, targetFrames: 2);
        short[] pcm = new short[AudioStreamFormat.FrameValues];

        Add(decoder, packets[0], packets[1]);
        decoder.Read(pcm);
        Add(decoder, packets[2..]);
        decoder.Read(pcm);

        // Trimmed to two queued frames, one of which just played.
        AudioStreamStats stats = decoder.TakeStats();
        Assert.Equal(9, stats.Skipped);
        Assert.Equal(1, stats.Depth);
    }

    [Fact]
    public void Extra_delay_after_a_stall_wears_off()
    {
        var (decoder, packets) = Stream(frames: 420, targetFrames: 2);

        for (int i = 0; i < 10; i++)
        {
            Tick(decoder, packets[i]);
        }

        // The player stalls for 30 ms: three packets queue up beyond the target, too few to be trimmed at once.
        Add(decoder, packets[10..13]);
        for (int i = 13; i < 420; i++)
        {
            Tick(decoder, packets[i]);
        }

        AudioStreamStats stats = decoder.TakeStats();
        Assert.Equal(3, stats.Skipped);
        Assert.Equal(1, stats.Depth);
    }

    private static (AudioStreamDecoder Decoder, byte[][] Packets) Stream(int frames, int targetFrames)
    {
        var encoder = new AudioStreamEncoder();
        float[] frame = new float[AudioStreamFormat.FrameValues];
        byte[][] packets = new byte[frames][];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < AudioStreamFormat.FrameSamples; i++)
            {
                float value = 0.3f * MathF.Sin(2 * MathF.PI * 440 * ((f * AudioStreamFormat.FrameSamples) + i) / AudioStreamFormat.SampleRate);
                frame[2 * i] = frame[(2 * i) + 1] = value;
            }

            byte[] buffer = new byte[AudioPacket.MaxSize];
            packets[f] = buffer[..encoder.Encode(frame, buffer)];
        }

        return (new AudioStreamDecoder(targetFrames), packets);
    }

    private static void Tick(AudioStreamDecoder decoder, byte[]? packet)
    {
        if (packet is not null)
        {
            decoder.Add(packet, 0);
        }

        decoder.Read(new short[AudioStreamFormat.FrameValues]);
    }

    private static void Add(AudioStreamDecoder decoder, params byte[][] packets)
    {
        foreach (byte[] packet in packets)
        {
            decoder.Add(packet, 0);
        }
    }
}
