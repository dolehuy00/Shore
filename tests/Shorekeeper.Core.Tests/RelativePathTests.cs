using Shorekeeper.Core.Transfers;

namespace Shorekeeper.Core.Tests;

public class RelativePathTests
{
    [Theory]
    [InlineData("app.zip", new[] { "app.zip" }, false)]
    [InlineData("build/logs/app.log", new[] { "build", "logs", "app.log" }, false)]
    [InlineData("build/logs/", new[] { "build", "logs" }, true)]
    [InlineData("Báo cáo quý 3.xlsx", new[] { "Báo cáo quý 3.xlsx" }, false)]
    public void Normal_paths_are_kept(string path, string[] expected, bool directory)
    {
        Assert.True(RelativePath.TrySanitize(path, out string[]? segments, out bool isDirectory));
        Assert.Equal(expected, segments);
        Assert.Equal(directory, isDirectory);
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("a/../../evil.exe")]
    [InlineData("./a")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("a\\..\\b")]
    [InlineData("file.txt:hidden")]
    [InlineData("a//b")]
    [InlineData("")]
    [InlineData("tab\tname")]
    [InlineData("...")]
    public void Escaping_or_malformed_paths_are_rejected(string path)
    {
        Assert.False(RelativePath.TrySanitize(path, out _, out _));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1.log", "_com1.log")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("name. . ", "name")]
    [InlineData("hoa-don\u202Efdp.exe", "hoa-donfdp.exe")]
    public void Awkward_names_are_made_safe(string name, string expected)
    {
        Assert.True(RelativePath.TrySanitize(name, out string[]? segments, out _));
        Assert.Equal(expected, Assert.Single(segments));
    }

    [Fact]
    public void Overly_long_paths_are_rejected()
    {
        Assert.False(RelativePath.TrySanitize(new string('a', 256), out _, out _));
        Assert.False(RelativePath.TrySanitize(string.Join('/', Enumerable.Repeat("a", 65)), out _, out _));
    }
}
