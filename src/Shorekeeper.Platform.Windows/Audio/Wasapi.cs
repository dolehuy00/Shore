using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>What capture and playback share: the speakers' audio client and the stream format.</summary>
internal static partial class Wasapi
{
    public const int SharedMode = 0;
    public const uint StreamFlagsAutoConvertPcm = 0x8000_0000;
    public const uint StreamFlagsSrcDefaultQuality = 0x0800_0000;

    private const int ClsctxAll = 0x17;
    private const int Render = 0;
    private const int Capture = 1;
    private const int Console = 0;
    private const uint DeviceStateActive = 1;
    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;
    // COM already initialised on this thread in the other model (the UI thread is STA): fine for what we do.
    private const int ChangedMode = unchecked((int)0x80010106);

    private static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    /// <summary>
    /// The audio client of the speakers with this id, or of the default ones when <paramref name="id"/> is null or
    /// that device is not active; COM is initialised for the calling thread first.
    /// </summary>
    public static IAudioClient OpenSpeakers(StrategyBasedComWrappers wrappers, string? id = null)
    {
        IMMDeviceEnumerator enumerator = Enumerator(wrappers);
        IMMDevice? device = null;
        if (id is not null && enumerator.GetDevice(id, out IMMDevice chosen) >= 0 && chosen.GetState(out uint state) >= 0 && state == DeviceStateActive)
        {
            device = chosen;
        }

        if (device is null)
        {
            ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(Render, Console, out device));
        }

        ThrowIfFailed(device.Activate(IidAudioClient, ClsctxAll, 0, out nint clientPointer));
        return Wrap<IAudioClient>(wrappers, clientPointer);
    }

    /// <summary>The audio client of the default microphone; COM is initialised for the calling thread first.</summary>
    /// <exception cref="COMException">There is no microphone.</exception>
    public static IAudioClient OpenMicrophone(StrategyBasedComWrappers wrappers)
    {
        ThrowIfFailed(Enumerator(wrappers).GetDefaultAudioEndpoint(Capture, Console, out IMMDevice device));
        ThrowIfFailed(device.Activate(IidAudioClient, ClsctxAll, 0, out nint clientPointer));
        return Wrap<IAudioClient>(wrappers, clientPointer);
    }

    /// <summary>The playback devices active now, with the names Windows shows.</summary>
    public static IReadOnlyList<AudioOutputDevice> ActiveSpeakers()
    {
        var wrappers = new StrategyBasedComWrappers();
        IMMDeviceEnumerator enumerator = Enumerator(wrappers);
        ThrowIfFailed(enumerator.EnumAudioEndpoints(Render, DeviceStateActive, out nint collectionPointer));
        var collection = Wrap<IMMDeviceCollection>(wrappers, collectionPointer);
        ThrowIfFailed(collection.GetCount(out uint count));
        List<AudioOutputDevice> devices = [];
        for (uint i = 0; i < count; i++)
        {
            ThrowIfFailed(collection.Item(i, out IMMDevice device));
            ThrowIfFailed(device.GetId(out nint idPointer));
            string id = Marshal.PtrToStringUni(idPointer) ?? "";
            Marshal.FreeCoTaskMem(idPointer);
            string name = id;
            if (device.OpenPropertyStore(StgmRead, out IPropertyStore store) >= 0 && store.GetValue(FriendlyName, out PropVariant value) >= 0)
            {
                if (value.Type == VtLpwstr && Marshal.PtrToStringUni(value.Pointer) is { Length: > 0 } shown)
                {
                    name = shown;
                }

                _ = PropVariantClear(ref value);
            }

            devices.Add(new AudioOutputDevice(id, name));
        }

        return devices;
    }

    private static IMMDeviceEnumerator Enumerator(StrategyBasedComWrappers wrappers)
    {
        int initialized = CoInitializeEx(0, 0);
        if (initialized != ChangedMode)
        {
            ThrowIfFailed(initialized);
        }

        ThrowIfFailed(CoCreateInstance(ClsidDeviceEnumerator, 0, ClsctxAll, IidDeviceEnumerator, out nint enumeratorPointer));
        return Wrap<IMMDeviceEnumerator>(wrappers, enumeratorPointer);
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

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);
}

internal readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

/// <summary>PROPVARIANT, for a string: its type, then the pointer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Type;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Pointer;
    public nint Pointer2;
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice(string id, out IMMDevice device);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, int context, nint activationParameters, out nint instance);

    [PreserveSig]
    int OpenPropertyStore(int access, out IPropertyStore store);

    /// <summary>The id, allocated with CoTaskMemAlloc.</summary>
    [PreserveSig]
    int GetId(out nint id);

    [PreserveSig]
    int GetState(out uint state);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant value);
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
