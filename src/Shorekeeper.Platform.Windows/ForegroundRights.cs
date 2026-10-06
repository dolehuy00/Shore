using System.Runtime.InteropServices;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// Windows only lets the process the user just started bring a window to the front. A second launch
/// (Start menu, Explorer's "Gửi bằng Shorekeeper…") passes that right on to the running instance.
/// </summary>
public static partial class ForegroundRights
{
    private const int AnyProcess = -1;

    public static void GrantToOtherProcesses() => AllowSetForegroundWindow(AnyProcess);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
