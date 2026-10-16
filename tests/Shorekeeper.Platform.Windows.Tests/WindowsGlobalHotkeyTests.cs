using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows.Tests;

public class WindowsGlobalHotkeyTests
{
    // Combinations nothing else on a test machine is likely to hold.
    private static readonly Hotkey First = Hotkey.Parse("Ctrl+Alt+Shift+F11")!;
    private static readonly Hotkey Second = Hotkey.Parse("Ctrl+Alt+Shift+9")!;

    [Fact]
    public void Registers_the_chosen_shortcut_and_lets_go_of_the_old_one()
    {
        using var ours = new WindowsGlobalHotkey(NullLogger<WindowsGlobalHotkey>.Instance);
        using var other = new WindowsGlobalHotkey(NullLogger<WindowsGlobalHotkey>.Instance);

        Assert.True(ours.Register(First));
        // A combination belongs to one registration at a time, so another one cannot take it now.
        Assert.False(other.Register(First));

        Assert.True(ours.Register(Second));
        Assert.True(other.Register(First));
        Assert.False(other.Register(Second));

        Assert.False(ours.Register(null));
        Assert.True(other.Register(Second));
    }
}
