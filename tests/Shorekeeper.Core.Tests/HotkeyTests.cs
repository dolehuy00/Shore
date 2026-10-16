using Shorekeeper.Core.Platform;

namespace Shorekeeper.Core.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+S", "Ctrl+Alt+S")]
    [InlineData(" shift + ctrl + k ", "Ctrl+Shift+K")]
    [InlineData("Alt+F12", "Alt+F12")]
    [InlineData("Ctrl+Alt+7", "Ctrl+Alt+7")]
    public void Parses_and_writes_back_in_one_order(string text, string expected) =>
        Assert.Equal(expected, Hotkey.Parse(text)?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S")]
    [InlineData("Shift+S")]
    [InlineData("Ctrl+Ctrl+S")]
    [InlineData("Win+S")]
    [InlineData("Ctrl+F13")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+Space")]
    [InlineData("Ctrl+Alt")]
    public void Rejects_what_would_get_in_the_way_of_typing_or_cannot_be_registered(string? text) =>
        Assert.Null(Hotkey.Parse(text));
}
