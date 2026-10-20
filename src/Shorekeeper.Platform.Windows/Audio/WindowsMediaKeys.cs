using System.Runtime.InteropServices;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>
/// Media keys pressed as if on the keyboard: the app that plays (browser, Spotify…) takes play/pause and the tracks,
/// Windows the volume, with its usual on-screen indicator.
/// </summary>
public sealed partial class WindowsMediaKeys : IMediaKeys
{
    private const uint KeyUp = 0x0002;

    public void Press(MediaKey key)
    {
        byte code = key switch
        {
            MediaKey.PlayPause => 0xB3,
            MediaKey.Next => 0xB0,
            MediaKey.Previous => 0xB1,
            MediaKey.VolumeUp => 0xAF,
            MediaKey.VolumeDown => 0xAE,
            MediaKey.Mute => 0xAD,
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        keybd_event(code, 0, 0, 0);
        keybd_event(code, 0, KeyUp, 0);
    }

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
}
