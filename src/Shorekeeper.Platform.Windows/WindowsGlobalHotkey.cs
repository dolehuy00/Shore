using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// Ctrl+Alt+S through <c>RegisterHotKey</c>. The shortcut belongs to a small thread with its own message loop,
/// so it works while every window is hidden in the tray.
/// </summary>
public sealed partial class WindowsGlobalHotkey(ILogger<WindowsGlobalHotkey> logger) : IGlobalHotkey, IDisposable
{
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModNoRepeat = 0x4000;
    private const uint KeyS = 0x53;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0;

    private readonly Lock gate = new();
    private Thread? thread;
    private uint threadId;

    public string DisplayText => "Ctrl+Alt+S";

    public event EventHandler? Pressed;

    public bool SetEnabled(bool enabled)
    {
        lock (gate)
        {
            if (enabled == thread is not null)
            {
                return enabled;
            }

            if (!enabled)
            {
                Stop();
                return false;
            }

            using var started = new ManualResetEventSlim();
            bool registered = false;
            var loop = new Thread(() =>
            {
                // Creates this thread's message queue before anyone posts to it.
                PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                threadId = GetCurrentThreadId();
                registered = RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModAlt | ModNoRepeat, KeyS);
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
                logger.LogWarning("{Hotkey} is already used by another program", DisplayText);
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
