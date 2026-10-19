using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>The speakers through WASAPI loopback, with Wi-Fi kept on its channel while it runs.</summary>
public sealed class WindowsAudioCapture : IAudioCapture
{
    public IDisposable Start(AudioFrameHandler onFrame)
    {
        WlanStreamingMode? wlan = WlanStreamingMode.TryEnable();
        var capture = new LoopbackCapture(onFrame);
        try
        {
            capture.Start();
        }
        catch
        {
            capture.Dispose();
            wlan?.Dispose();
            throw;
        }

        return new Running(capture, wlan);
    }

    private sealed class Running(LoopbackCapture capture, WlanStreamingMode? wlan) : IDisposable
    {
        public void Dispose()
        {
            capture.Dispose();
            wlan?.Dispose();
        }
    }
}
