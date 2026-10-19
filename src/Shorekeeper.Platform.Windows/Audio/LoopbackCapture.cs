using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Shorekeeper.Core.Audio;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>One 10 ms frame of <see cref="AudioStreamFormat.FrameValues"/> interleaved samples; only valid during the call.</summary>
public delegate void AudioFrameHandler(ReadOnlySpan<float> frame);

/// <summary>
/// What the default speakers play, through WASAPI loopback, as 48 kHz stereo float in 10 ms frames. Windows converts
/// from the device's own format. While nothing plays Windows delivers nothing, so silence is filled in on the clock:
/// the receiver sees an unbroken stream.
/// </summary>
public sealed partial class LoopbackCapture(AudioFrameHandler onFrame) : IDisposable
{
    private const int ClsctxAll = 0x17;
    private const int SharedMode = 0;
    private const uint StreamFlagsLoopback = 0x0002_0000;
    private const uint StreamFlagsAutoConvertPcm = 0x8000_0000;
    private const uint StreamFlagsSrcDefaultQuality = 0x0800_0000;
    private const uint BufferFlagsSilent = 0x2;
    private const long BufferDuration = 20 * 10_000;
    private const uint TimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F_0003;
    // Polling every 2 ms: loopback's event mode does not fire while nothing plays.
    private const long PollInterval = -2 * 10_000;

    private static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

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
            ThrowIfFailed(CoInitializeEx(0, 0));
            var wrappers = new StrategyBasedComWrappers();
            var enumerator = Wrap<IMMDeviceEnumerator>(wrappers, CreateInstance(ClsidDeviceEnumerator, IidDeviceEnumerator));
            ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(0, 0, out IMMDevice device));
            ThrowIfFailed(device.Activate(IidAudioClient, ClsctxAll, 0, out nint clientPointer));
            client = Wrap<IAudioClient>(wrappers, clientPointer);

            format = Marshal.AllocCoTaskMem(40);
            WriteFloatStereoFormat(format);
            ThrowIfFailed(client.Initialize(SharedMode, StreamFlagsLoopback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality, BufferDuration, 0, format, 0));
            ThrowIfFailed(client.GetService(IidCaptureClient, out nint capturePointer));
            var capture = Wrap<IAudioCaptureClient>(wrappers, capturePointer);
            timer = CreateWaitableTimerExW(0, 0, TimerHighResolution, TimerAllAccess);
            ThrowIfFailed(client.Start());
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
                ThrowIfFailed(capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _));
                Append(data, (int)frames, (flags & BufferFlagsSilent) != 0);
                ThrowIfFailed(capture.ReleaseBuffer(frames));
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

    private static void WriteFloatStereoFormat(nint format)
    {
        // WAVEFORMATEXTENSIBLE: 48 kHz, 2 channels, 32-bit float, front left + right.
        const int channels = AudioStreamFormat.Channels;
        Marshal.WriteInt16(format, 0, unchecked((short)0xFFFE));
        Marshal.WriteInt16(format, 2, channels);
        Marshal.WriteInt32(format, 4, AudioStreamFormat.SampleRate);
        Marshal.WriteInt32(format, 8, AudioStreamFormat.SampleRate * channels * 4);
        Marshal.WriteInt16(format, 12, channels * 4);
        Marshal.WriteInt16(format, 14, 32);
        Marshal.WriteInt16(format, 16, 22);
        Marshal.WriteInt16(format, 18, 32);
        Marshal.WriteInt32(format, 20, 0x3);
        Marshal.StructureToPtr(SubtypeFloat, format + 24, false);
    }

    private static nint CreateInstance(Guid clsid, Guid iid)
    {
        ThrowIfFailed(CoCreateInstance(clsid, 0, ClsctxAll, iid, out nint pointer));
        return pointer;
    }

    private static T Wrap<T>(StrategyBasedComWrappers wrappers, nint pointer)
    {
        try
        {
            return (T)wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, int context, in Guid iid, out nint instance);

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

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, int context, nint activationParameters, out nint instance);
}

[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint frames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint frames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, nint format, out nint closest);

    [PreserveSig]
    int GetMixFormat(out nint format);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(nint handle);

    [PreserveSig]
    int GetService(in Guid iid, out nint service);
}

[GeneratedComInterface]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
internal partial interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong performanceCounterPosition);

    [PreserveSig]
    int ReleaseBuffer(uint frames);

    [PreserveSig]
    int GetNextPacketSize(out uint frames);
}
