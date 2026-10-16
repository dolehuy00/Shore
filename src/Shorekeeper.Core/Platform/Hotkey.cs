namespace Shorekeeper.Core.Platform;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// A system-wide shortcut such as "Ctrl+Alt+S": Ctrl or Alt (plus Shift if wanted) and a letter, digit or F1–F12,
/// so it never swallows plain typing.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, string Key)
{
    public static Hotkey Default { get; } = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "S");

    /// <summary>"A"–"Z", "0"–"9" or "F1"–"F12".</summary>
    public static bool IsValidKey(string key) =>
        key.Length == 1 ? char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0])
        : key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int n) && n is >= 1 and <= 12 && key[1] != '0';

    public static Hotkey? Create(HotkeyModifiers modifiers, string key) =>
        (modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) != 0 && IsValidKey(key) ? new Hotkey(modifiers, key) : null;

    /// <summary>"Ctrl+Alt+S", in any case and spacing; null when it is not a shortcut <see cref="Create"/> allows.</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (string part in parts[..^1])
        {
            HotkeyModifiers modifier = part.ToUpperInvariant() switch
            {
                "CTRL" => HotkeyModifiers.Ctrl,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return null;
            }

            modifiers |= modifier;
        }

        return Create(modifiers, parts[^1].ToUpperInvariant());
    }

    public override string ToString()
    {
        string text = "";
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            text += "Ctrl+";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            text += "Alt+";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            text += "Shift+";
        }

        return text + Key;
    }
}
