using System.Runtime.InteropServices;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>
/// While open, keeps every connected Wi-Fi adapter on its channel: media streaming mode on and background scans off,
/// as game-streaming apps do. A scan takes the radio off the channel for 100–200 ms, which a live stream hears as a
/// gap. Windows restores both settings when the handle closes, also if the process ends.
/// </summary>
public sealed partial class WlanStreamingMode : IDisposable
{
    private const uint ClientVersion = 2;
    private const int OpcodeBackgroundScanEnabled = 2;
    private const int OpcodeMediaStreamingMode = 3;
    private const int StateConnected = 1;
    // WLAN_INTERFACE_INFO: GUID, WCHAR[256] description, WLAN_INTERFACE_STATE.
    private const int InterfaceInfoSize = 16 + 512 + 4;

    private nint handle;

    private WlanStreamingMode(nint handle, int adapters)
    {
        this.handle = handle;
        Adapters = adapters;
    }

    /// <summary>Connected Wi-Fi adapters now in streaming mode.</summary>
    public int Adapters { get; }

    /// <summary>Null without Wi-Fi (the WLAN service is not running).</summary>
    public static WlanStreamingMode? TryEnable()
    {
        if (WlanOpenHandle(ClientVersion, 0, out _, out nint handle) != 0)
        {
            return null;
        }

        int adapters = 0;
        if (WlanEnumInterfaces(handle, 0, out nint list) == 0)
        {
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    nint info = list + 8 + (i * InterfaceInfoSize);
                    if (Marshal.ReadInt32(info + 16 + 512) != StateConnected)
                    {
                        continue;
                    }

                    Guid adapter = Marshal.PtrToStructure<Guid>(info);
                    bool streaming = Set(handle, adapter, OpcodeMediaStreamingMode, 1);
                    bool noScans = Set(handle, adapter, OpcodeBackgroundScanEnabled, 0);
                    adapters += streaming || noScans ? 1 : 0;
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }

        return new WlanStreamingMode(handle, adapters);
    }

    public void Dispose()
    {
        if (handle != 0)
        {
            _ = WlanCloseHandle(handle, 0);
            handle = 0;
        }
    }

    private static unsafe bool Set(nint handle, Guid adapter, int opcode, int value) =>
        WlanSetInterface(handle, in adapter, opcode, sizeof(int), &value, 0) == 0;

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint clientHandle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint clientHandle, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint interfaceList);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanSetInterface(nint clientHandle, in Guid interfaceGuid, int opCode, int dataSize, void* data, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
