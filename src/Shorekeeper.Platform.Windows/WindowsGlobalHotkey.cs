using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// The "Gửi nhanh" shortcut through <c>RegisterHotKey</c>. The shortcut belongs to a small thread with its own message loop,
/// so it works while every window is hidden in the tray.
/// </summary>
public sealed partial class WindowsGlobalHotkey(ILogger<WindowsGlobalHotkey> logger) : IGlobalHotkey, IDisposable
{
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModNoRepeat = 0x4000;
    private const uint KeyF1 = 0x70;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0;

    private readonly Lock gate = new();
    private Thread? thread;
    private uint threadId;
    private Hotkey? current;

    public event EventHandler? Pressed;

    public bool Register(Hotkey? hotkey)
    {
        lock (gate)
        {
            if (hotkey == current && (hotkey is null || thread is not null))
            {
                return thread is not null;
            }

            Stop();
            current = hotkey;
            if (hotkey is null)
            {
                return false;
            }

            (uint modifiers, uint key) = Win32(hotkey);
            using var started = new ManualResetEventSlim();
            bool registered = false;
            var loop = new Thread(() =>
            {
                // Creates this thread's message queue before anyone posts to it.
                PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                threadId = GetCurrentThreadId();
                registered = RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers | ModNoRepeat, key);
                started.Set();
                if (registered)
                {
                    Run();
                }
            })
            {
                IsBackground = true,
                Name = "Shorekeeper hotkey",
            };
            loop.Start();
            started.Wait();

            if (!registered)
            {
                loop.Join();
                logger.LogWarning("{Hotkey} is already used by another program", hotkey);
                return false;
            }

            thread = loop;
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            Stop();
        }
    }

    private static (uint Modifiers, uint Key) Win32(Hotkey hotkey)
    {
        uint modifiers = (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl) ? ModControl : 0)
            | (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt) ? ModAlt : 0)
            | (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift) ? ModShift : 0);
        // Letters and digits are their own virtual-key codes; F1–F12 follow one another from 0x70.
        uint key = hotkey.Key.Length == 1 ? hotkey.Key[0] : KeyF1 + uint.Parse(hotkey.Key.AsSpan(1), CultureInfo.InvariantCulture) - 1;
        return (modifiers, key);
    }

    private void Run()
    {
        while (GetMessage(out Message message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Id == WmHotkey)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
        }

        UnregisterHotKey(IntPtr.Zero, HotkeyId);
    }

    private void Stop()
    {
        if (thread is null)
        {
            return;
        }

        PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        thread.Join();
        thread = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Message message, IntPtr window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out Message message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
