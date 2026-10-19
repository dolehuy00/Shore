using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Shorekeeper.Core.Audio;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>What capture and playback share: the default speakers' audio client and the stream format.</summary>
internal static partial class Wasapi
{
    public const int SharedMode = 0;
    public const uint StreamFlagsAutoConvertPcm = 0x8000_0000;
    public const uint StreamFlagsSrcDefaultQuality = 0x0800_0000;

    private const int ClsctxAll = 0x17;
    private const int Render = 0;
    private const int Console = 0;

    private static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>The default speakers' audio client; COM is initialised for the calling thread first.</summary>
    public static IAudioClient OpenSpeakers(StrategyBasedComWrappers wrappers)
    {
        ThrowIfFailed(CoInitializeEx(0, 0));
        ThrowIfFailed(CoCreateInstance(ClsidDeviceEnumerator, 0, ClsctxAll, IidDeviceEnumerator, out nint enumeratorPointer));
        var enumerator = Wrap<IMMDeviceEnumerator>(wrappers, enumeratorPointer);
        ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(Render, Console, out IMMDevice device));
        ThrowIfFailed(device.Activate(IidAudioClient, ClsctxAll, 0, out nint clientPointer));
        return Wrap<IAudioClient>(wrappers, clientPointer);
    }

    public static T Service<T>(StrategyBasedComWrappers wrappers, IAudioClient client, Guid iid)
    {
        ThrowIfFailed(client.GetService(iid, out nint pointer));
        return Wrap<T>(wrappers, pointer);
    }

    /// <summary>WAVEFORMATEXTENSIBLE: 48 kHz, 2 channels, 32-bit float, front left + right (40 bytes).</summary>
    public static nint FloatStereoFormat()
    {
        const int channels = AudioStreamFormat.Channels;
        nint format = Marshal.AllocCoTaskMem(40);
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
        return format;
    }

    /// <summary>WAVEFORMATEX: 48 kHz, 2 channels, 16-bit PCM (18 bytes).</summary>
    public static nint Pcm16StereoFormat()
    {
        const int channels = AudioStreamFormat.Channels;
        nint format = Marshal.AllocCoTaskMem(18);
        Marshal.WriteInt16(format, 0, 1);
        Marshal.WriteInt16(format, 2, channels);
        Marshal.WriteInt32(format, 4, AudioStreamFormat.SampleRate);
        Marshal.WriteInt32(format, 8, AudioStreamFormat.SampleRate * channels * 2);
        Marshal.WriteInt16(format, 12, channels * 2);
        Marshal.WriteInt16(format, 14, 16);
        Marshal.WriteInt16(format, 16, 0);
        return format;
    }

    public static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
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

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, int context, in Guid iid, out nint instance);
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

[GeneratedComInterface]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal partial interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint frames, out nint data);

    [PreserveSig]
    int ReleaseBuffer(uint frames, uint flags);
}
