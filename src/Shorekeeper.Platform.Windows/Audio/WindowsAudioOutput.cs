using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>
/// "Nghe" on a computer: the sound through the chosen device ("Phát ra") or the default speakers, WASAPI shared mode
/// driven by the device's events. Only two frames wait in the device's buffer; the thread runs at "Pro Audio" priority
/// so it is never late for them. When the default speakers change (headphones plugged in, Bluetooth), it moves to the
/// new default; when the chosen device goes away, to the default.
/// </summary>
public sealed partial class WindowsAudioOutput : IAudioOutput
{
    private const uint StreamFlagsEventCallback = 0x0004_0000;
    private const long BufferDuration = 30 * 10_000;
    private const int TargetFrames = 2 * AudioStreamFormat.FrameSamples;

    private static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    public IDisposable Start(AudioFrameReader read, string title, Action stopRequested, Action<MediaKey>? keyRequested = null, string? device = null) =>
        new Playing(read, device);

    private sealed class Playing : IDisposable
    {
        private readonly AudioFrameReader read;
        private readonly string? device;
        private readonly WlanStreamingMode? wlan = WlanStreamingMode.TryEnable();
        private readonly Thread thread;
        private volatile bool stopping;

        public Playing(AudioFrameReader read, string? device)
        {
            this.read = read;
            this.device = device;
            thread = new Thread(Run) { IsBackground = true, Name = "Audio play" };
            thread.Start();
        }

        public void Dispose()
        {
            stopping = true;
            thread.Join();
            wlan?.Dispose();
        }

        private void Run()
        {
            uint task = 0;
            nint mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref task);
            short[] pcm = new short[AudioStreamFormat.FrameValues];
            nint wake = CreateEventW(0, false, false, 0);
            try
            {
                while (!stopping)
                {
                    try
                    {
                        Play(pcm, wake);
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException)
                    {
                        // The speakers went away or changed; try the new default in a moment.
                        Thread.Sleep(200);
                    }
                }
            }
            finally
            {
                CloseHandle(wake);
                if (mmcss != 0)
                {
                    AvRevertMmThreadCharacteristics(mmcss);
                }
            }
        }

        private void Play(short[] pcm, nint wake)
        {
            var wrappers = new StrategyBasedComWrappers();
            IAudioClient client = Wasapi.OpenSpeakers(wrappers, device);
            nint format = Wasapi.Pcm16StereoFormat();
            try
            {
                Wasapi.ThrowIfFailed(client.Initialize(
                    Wasapi.SharedMode, StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality,
                    BufferDuration, 0, format, 0));
                Wasapi.ThrowIfFailed(client.SetEventHandle(wake));
                Wasapi.ThrowIfFailed(client.GetBufferSize(out uint bufferFrames));
                var render = Wasapi.Service<IAudioRenderClient>(wrappers, client, IidRenderClient);
                uint target = Math.Min(bufferFrames, TargetFrames);
                Wasapi.ThrowIfFailed(client.Start());
                try
                {
                    while (!stopping)
                    {
                        Wasapi.ThrowIfFailed(client.GetCurrentPadding(out uint padding));
                        while (padding + AudioStreamFormat.FrameSamples <= target)
                        {
                            read(pcm);
                            Wasapi.ThrowIfFailed(render.GetBuffer(AudioStreamFormat.FrameSamples, out nint data));
                            Marshal.Copy(pcm, 0, data, pcm.Length);
                            Wasapi.ThrowIfFailed(render.ReleaseBuffer(AudioStreamFormat.FrameSamples, 0));
                            padding += AudioStreamFormat.FrameSamples;
                        }

                        _ = WaitForSingleObject(wake, 100);
                    }
                }
                finally
                {
                    client.Stop();
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(format);
            }
        }
    }

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
