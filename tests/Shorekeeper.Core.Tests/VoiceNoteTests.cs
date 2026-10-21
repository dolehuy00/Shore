using Shorekeeper.Core.Audio;

namespace Shorekeeper.Core.Tests;

/// <summary>Voice messages: recording into Ogg Opus and playing back (docs/15-chat.md §3).</summary>
public sealed class VoiceNoteTests
{
    [Fact]
    public void A_recording_plays_back_as_long_as_it_was_and_as_loud()
    {
        VoiceNote note = Record(seconds: 2, amplitude: 0.5f);

        Assert.Equal(2000, note.DurationMs);
        Assert.Equal("OggS"u8.ToArray(), note.Ogg[..4]);
        // Speech at 24 kbps: about 3 KB a second.
        Assert.InRange(note.Ogg.Length, 4_000, 9_000);

        VoiceNotePlayer player = VoiceNotePlayer.Open(note.Ogg)!;
        short[] frame = new short[AudioStreamFormat.FrameValues];
        int loud = 0;
        while (!player.HasEnded)
        {
            player.Read(frame);
            loud = Math.Max(loud, frame.Max(s => Math.Abs((int)s)));
            Assert.Equal(frame[0], frame[1]);
        }

        Assert.InRange(player.PositionMs, 1980, 2020);
        Assert.InRange(loud, 10_000, 24_000);

        // Then silence, for as long as the output keeps asking.
        player.Read(frame);
        Assert.All(frame, s => Assert.Equal(0, s));
    }

    [Fact]
    public void The_waveform_follows_the_loudness()
    {
        var recorder = new VoiceRecorder();
        Feed(recorder, seconds: 1, amplitude: 0.05f);
        Feed(recorder, seconds: 1, amplitude: 0.8f);

        byte[] wave = recorder.Finish().Wave;

        Assert.Equal(VoiceRecorder.WaveBars, wave.Length);
        Assert.Equal(255, wave.Max());
        Assert.True(wave[..20].Max() < 60, "the quiet half stays low");
        Assert.True(wave[28..].Min() > 200, "the loud half reaches the top");
    }

    [Fact]
    public void Silence_has_a_flat_waveform()
    {
        Assert.All(Record(seconds: 1, amplitude: 0).Wave, bar => Assert.Equal(0, bar));
    }

    [Fact]
    public void Recording_stops_at_five_minutes()
    {
        var recorder = new VoiceRecorder();
        Feed(recorder, seconds: 301, amplitude: 0.1f);

        Assert.True(recorder.IsFull);
        Assert.Equal(VoiceRecorder.MaxDurationMs, recorder.Finish().DurationMs);
    }

    [Fact]
    public void Long_packets_cross_segments_and_pages()
    {
        byte[][] packets = [.. Enumerable.Range(0, 120).Select(i => Enumerable.Repeat((byte)i, i * 7).ToArray())];

        byte[] file = OggOpus.Write(new OpusClip(2, 312, packets), 960);
        OpusClip read = OggOpus.Read(file)!;

        Assert.Equal((2, 312), (read.Channels, read.PreSkip));
        Assert.Equal(packets, read.Packets);
    }

    [Fact]
    public void A_damaged_file_is_refused()
    {
        byte[] file = Record(seconds: 1, amplitude: 0.3f).Ogg;

        byte[] flipped = [.. file];
        flipped[^10] ^= 0xFF;
        Assert.Null(OggOpus.Read(flipped));
        Assert.Null(OggOpus.Read(file.AsSpan()[..^5]));
        Assert.Null(VoiceNotePlayer.Open("not a voice message"u8.ToArray()));
    }

    private static VoiceNote Record(int seconds, float amplitude)
    {
        var recorder = new VoiceRecorder();
        Feed(recorder, seconds, amplitude);
        return recorder.Finish();
    }

    /// <summary>A 440 Hz tone, as the microphone would deliver it: 10 ms stereo frames.</summary>
    private static void Feed(VoiceRecorder recorder, int seconds, float amplitude)
    {
        float[] frame = new float[AudioStreamFormat.FrameValues];
        for (int f = 0; f < seconds * 100; f++)
        {
            for (int i = 0; i < AudioStreamFormat.FrameSamples; i++)
            {
                float value = amplitude * MathF.Sin(2 * MathF.PI * 440 * ((f * AudioStreamFormat.FrameSamples) + i) / AudioStreamFormat.SampleRate);
                frame[2 * i] = value;
                frame[(2 * i) + 1] = value;
            }

            recorder.Add(frame);
        }
    }
}
