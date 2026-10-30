namespace Shorekeeper.Core.Audio;

/// <summary>
/// "Âm lượng" of what this device listens to (docs/05-protocol.md §4.7): its own level, apart from the system's and the
/// source's. Scales interleaved stereo frames in percent (100 as it comes), gliding from the level of the frame before
/// so a change does not click, and holding loud sound at full scale instead of wrapping around.
/// </summary>
public static class AudioGain
{
    public static void Apply(Span<short> pcm, int fromPercent, int toPercent)
    {
        if (fromPercent == 100 && toPercent == 100)
        {
            return;
        }

        float from = fromPercent / 100f;
        float step = (toPercent - fromPercent) / 100f / Math.Max(1, pcm.Length);
        for (int i = 0; i < pcm.Length; i++)
        {
            // Both channels of a frame get the same level.
            float gain = from + (step * (i & ~1));
            pcm[i] = (short)Math.Clamp(pcm[i] * gain, short.MinValue, short.MaxValue);
        }
    }
}
