namespace Shorekeeper.Core.Platform;

/// <summary>The system-wide shortcut that opens "Gửi nhanh" (docs/10-ux.md §10).</summary>
public interface IGlobalHotkey
{
    /// <summary>Shown in the UI, e.g. "Ctrl+Alt+S".</summary>
    string DisplayText { get; }

    /// <summary>Raised on a background thread.</summary>
    event EventHandler? Pressed;

    /// <returns>Whether the shortcut is registered afterwards: false when disabling, or when another program already uses it.</returns>
    bool SetEnabled(bool enabled);
}

public sealed class NullGlobalHotkey : IGlobalHotkey
{
    public static NullGlobalHotkey Instance { get; } = new();

    public string DisplayText => "";

    public event EventHandler? Pressed
    {
        add { }
        remove { }
    }

    public bool SetEnabled(bool enabled) => false;
}
