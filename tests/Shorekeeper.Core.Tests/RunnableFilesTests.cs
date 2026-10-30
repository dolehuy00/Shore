using Shorekeeper.Core.Transfers;

namespace Shorekeeper.Core.Tests;

public class RunnableFilesTests
{
    [Theory]
    [InlineData(@"C:\Nhận\setup.exe")]
    [InlineData("hoa-don.pdf.EXE")]
    [InlineData("chay.bat")]
    [InlineData("Tài liệu.lnk")]
    [InlineData("cap-nhat.ps1")]
    [InlineData("anh.js")]
    [InlineData("bo-cai.msi")]
    [InlineData("dia.iso")]
    public void Programs_scripts_and_shortcuts_ask_first(string path) => Assert.True(RunnableFiles.Is(path));

    [Theory]
    [InlineData("bao-cao.pdf")]
    [InlineData("anh.jpg")]
    [InlineData("so-lieu.xlsx")]
    [InlineData("exe")]
    [InlineData("ban-build.zip")]
    [InlineData("README")]
    public void Documents_and_pictures_open_at_once(string path) => Assert.False(RunnableFiles.Is(path));
}
