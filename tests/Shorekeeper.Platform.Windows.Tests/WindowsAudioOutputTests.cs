using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Shorekeeper.Core.Audio;
using Shorekeeper.Platform.Windows.Audio;

namespace Shorekeeper.Platform.Windows.Tests;

public sealed class WindowsAudioOutputTests
{
    [Fact]
    public async Task Pulls_frames_at_the_speakers_pace()
    {
        try
        {
            Wasapi.OpenSpeakers(new StrategyBasedComWrappers());
        }
        catch (COMException)
        {
            Assert.Skip("No speakers on this machine");
        }

        int frames = 0;
        // Silence: the test only measures the pace, nobody should hear it.
        using (new WindowsAudioOutput().Start(pcm => { pcm.Clear(); Interlocked.Increment(ref frames); }, "", () => { }))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        // 100 frames a second, plus the two queued at the start.
        Assert.InRange(frames, 180, 220);
    }
}
