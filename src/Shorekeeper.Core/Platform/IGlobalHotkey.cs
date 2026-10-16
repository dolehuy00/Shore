namespace Shorekeeper.Core.Platform;

/// <summary>The system-wide shortcut that opens "Gửi nhanh" (docs/10-ux.md §10).</summary>
public interface IGlobalHotkey
{
    /// <summary>Raised on a background thread.</summary>
    event EventHandler? Pressed;

    /// <summary>Registers <paramref name="hotkey"/> in place of the current one, or none when null.</summary>
    /// <returns>Whether a shortcut is registered afterwards: false when null, or when another program already uses it.</returns>
    bool Register(Hotkey? hotkey);
}

public sealed class NullGlobalHotkey : IGlobalHotkey
{
    public static NullGlobalHotkey Instance { get; } = new();

    public event EventHandler? Pressed
    {
        add { }
        remove { }
    }

    public bool Register(Hotkey? hotkey) => false;
}
