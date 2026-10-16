using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Desktop.Views;

/// <summary>
/// A button showing a shortcut ("Ctrl+Alt+S"): clicked, it waits for the next allowed key combination and takes it;
/// Esc or leaving it gives up.
/// </summary>
public sealed class ShortcutRecorder : Button
{
    public static readonly StyledProperty<string> ShortcutProperty =
        AvaloniaProperty.Register<ShortcutRecorder, string>(nameof(Shortcut), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsRecordingProperty =
        AvaloniaProperty.Register<ShortcutRecorder, bool>(nameof(IsRecording), defaultBindingMode: BindingMode.TwoWay);

    static ShortcutRecorder()
    {
        ShortcutProperty.Changed.AddClassHandler<ShortcutRecorder>((recorder, _) => recorder.ShowShortcut());
        IsRecordingProperty.Changed.AddClassHandler<ShortcutRecorder>((recorder, _) => recorder.ShowShortcut());
    }

    public string Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    public bool IsRecording
    {
        get => GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnClick()
    {
        base.OnClick();
        IsRecording = !IsRecording;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnKeyDown(e);
            return;
        }

        // Every key belongs to the recording: Space and Enter do not click, Tab does not move on.
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            IsRecording = false;
            return;
        }

        if (KeyName(e.Key) is { } key && Hotkey.Create(Modifiers(e.KeyModifiers), key) is { } hotkey)
        {
            Shortcut = hotkey.ToString();
            IsRecording = false;
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        IsRecording = false;
    }

    private void ShowShortcut() => Content = IsRecording ? L.T("Nhấn phím…") : Shortcut;

    private static HotkeyModifiers Modifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? HotkeyModifiers.Ctrl : HotkeyModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Alt) ? HotkeyModifiers.Alt : HotkeyModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Shift) ? HotkeyModifiers.Shift : HotkeyModifiers.None);

    /// <summary>The name <see cref="Hotkey"/> uses for <paramref name="key"/>, or null for keys it does not take.</summary>
    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z or >= Key.F1 and <= Key.F12 => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };
}
