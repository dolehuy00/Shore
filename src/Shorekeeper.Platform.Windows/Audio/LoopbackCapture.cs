using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Shorekeeper.Core.Audio;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>
/// What the default speakers play, through WASAPI loopback, as 48 kHz stereo float in 10 ms frames. Windows converts
/// from the device's own format. While nothing plays Windows delivers nothing, so silence is filled in on the clock:
/// the receiver sees an unbroken stream.
/// </summary>
public sealed partial class LoopbackCapture(AudioFrameHandler onFrame) : IDisposable
{
    private const uint StreamFlagsLoopback = 0x0002_0000;
    private const uint BufferFlagsSilent = 0x2;
    private const long BufferDuration = 20 * 10_000;
    private const uint TimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F_0003;
    // Polling every 2 ms: loopback's event mode does not fire while nothing plays.
    private const long PollInterval = -2 * 10_000;

    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly float[] frame = new float[AudioStreamFormat.FrameValues];
    private readonly ManualResetEventSlim ready = new();
    private Thread? thread;
    private volatile bool stopping;
    private Exception? failure;
    private int filled;

    /// <summary>Frames of silence filled in because nothing played.</summary>
    public long SilentFrames { get; private set; }

    /// <summary>Starts capturing; throws when the speakers cannot be opened.</summary>
    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Loopback capture", Priority = ThreadPriority.Highest };
        thread.Start();
        ready.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException($"Cannot capture the speakers: {failure.Message}", failure);
        }
    }

    public void Dispose()
    {
        stopping = true;
        thread?.Join();
        ready.Dispose();
    }

    private void Run()
    {
        nint format = 0;
        nint timer = 0;
        IAudioClient? client = null;
        try
        {
            var wrappers = new StrategyBasedComWrappers();
            client = Wasapi.OpenSpeakers(wrappers);
            format = Wasapi.FloatStereoFormat();
            Wasapi.ThrowIfFailed(client.Initialize(
                Wasapi.SharedMode, StreamFlagsLoopback | Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality, BufferDuration, 0, format, 0));
            var capture = Wasapi.Service<IAudioCaptureClient>(wrappers, client, IidCaptureClient);
            timer = CreateWaitableTimerExW(0, 0, TimerHighResolution, TimerAllAccess);
            Wasapi.ThrowIfFailed(client.Start());
            ready.Set();
            Capture(capture, timer);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
        {
            failure = ex;
        }
        finally
        {
            client?.Stop();
            if (timer != 0)
            {
                CloseHandle(timer);
            }

            Marshal.FreeCoTaskMem(format);
            ready.Set();
        }
    }

    private void Capture(IAudioCaptureClient capture, nint timer)
    {
        long lastData = Stopwatch.GetTimestamp();
        long silentUntil = lastData;
        long gap = Stopwatch.Frequency / 50;
        while (!stopping)
        {
            SetWaitableTimer(timer, PollInterval, 0, 0, 0, false);
            _ = WaitForSingleObject(timer, 100);

            bool any = false;
            while (capture.GetNextPacketSize(out uint size) == 0 && size > 0)
            {
                Wasapi.ThrowIfFailed(capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _));
                Append(data, (int)frames, (flags & BufferFlagsSilent) != 0);
                Wasapi.ThrowIfFailed(capture.ReleaseBuffer(frames));
                any = true;
            }

            long now = Stopwatch.GetTimestamp();
            if (any)
            {
                lastData = silentUntil = now;
            }
            else if (now - lastData > gap)
            {
                // Nothing plays: keep the stream going with silence, at the clock's pace.
                int samples = (int)((now - silentUntil) * AudioStreamFormat.SampleRate / Stopwatch.Frequency);
                Append(0, samples, silent: true);
                silentUntil += samples * Stopwatch.Frequency / AudioStreamFormat.SampleRate;
            }
        }
    }

    private unsafe void Append(nint data, int samples, bool silent)
    {
        var source = new ReadOnlySpan<float>((void*)data, silent ? 0 : samples * AudioStreamFormat.Channels);
        int offset = 0;
        int total = samples * AudioStreamFormat.Channels;
        while (offset < total)
        {
            int take = Math.Min(frame.Length - filled, total - offset);
            if (silent)
            {
                frame.AsSpan(filled, take).Clear();
            }
            else
            {
                source.Slice(offset, take).CopyTo(frame.AsSpan(filled));
            }

            filled += take;
            offset += take;
            if (filled == frame.Length)
            {
                SilentFrames += silent ? 1 : 0;
                onFrame(frame);
                filled = 0;
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
