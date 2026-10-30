using Shorekeeper.Core.Audio;

namespace Shorekeeper.Core.Tests;

public class AudioGainTests
{
    [Fact]
    public void Full_level_leaves_the_sound_as_it_is()
    {
        short[] pcm = [1000, -1000, 32767, -32768];

        AudioGain.Apply(pcm, 100, 100);

        Assert.Equal(new short[] { 1000, -1000, 32767, -32768 }, pcm);
    }

    [Fact]
    public void Half_level_halves_and_none_is_silence()
    {
        short[] half = [1000, -1000, 2000, -2000];
        short[] none = [1000, -1000, 2000, -2000];

        AudioGain.Apply(half, 50, 50);
        AudioGain.Apply(none, 0, 0);

        Assert.Equal(new short[] { 500, -500, 1000, -1000 }, half);
        Assert.All(none, v => Assert.Equal((short)0, v));
    }

    [Fact]
    public void Louder_than_it_came_stops_at_full_scale()
    {
        short[] pcm = [30000, -30000, 100, -100];

        AudioGain.Apply(pcm, 150, 150);

        Assert.Equal(new short[] { 32767, -32768, 150, -150 }, pcm);
    }

    [Fact]
    public void A_change_glides_across_the_frame_the_same_for_both_channels()
    {
        short[] pcm = [.. Enumerable.Repeat((short)10000, 960)];

        AudioGain.Apply(pcm, 100, 0);

        Assert.Equal(10000, pcm[0]);
        Assert.InRange(pcm[^1], (short)0, (short)50);
        for (int i = 0; i < pcm.Length; i += 2)
        {
            Assert.Equal(pcm[i], pcm[i + 1]);
            Assert.True(i == 0 || pcm[i] <= pcm[i - 2]);
        }
    }
}
