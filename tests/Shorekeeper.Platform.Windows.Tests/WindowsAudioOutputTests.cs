using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Platform;
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

    [Fact]
    public async Task Plays_on_the_default_speakers_when_the_chosen_device_is_gone()
    {
        if (new WindowsAudioOutputDevices().GetAll().Count == 0)
        {
            Assert.Skip("No speakers on this machine");
        }

        int frames = 0;
        using (new WindowsAudioOutput().Start(pcm => { pcm.Clear(); Interlocked.Increment(ref frames); }, "", () => { }, device: "{0.0.0.00000000}.{no-such-device}"))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        Assert.True(frames > 50, $"{frames} frames");
    }

    [Fact]
    public void Lists_the_active_playback_devices_by_name()
    {
        IReadOnlyList<AudioOutputDevice> devices = new WindowsAudioOutputDevices().GetAll();
        if (devices.Count == 0)
        {
            Assert.Skip("No speakers on this machine");
        }

        Assert.All(devices, device =>
        {
            Assert.NotEmpty(device.Id);
            Assert.NotEqual(device.Id, device.Name);
        });
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(" | ", devices.Select(d => d.Name)));
    }
}
