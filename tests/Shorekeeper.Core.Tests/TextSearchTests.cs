using Shorekeeper.Core.Text;

namespace Shorekeeper.Core.Tests;

public class TextSearchTests
{
    [Theory]
    [InlineData("thang 9", true)]
    [InlineData("THÁNG", true)]
    [InlineData("bao cao", true)]
    [InlineData("pdf thang", true)]
    [InlineData("thang 10", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Matches_ignores_case_and_accents_and_needs_every_word(string query, bool expected)
    {
        Assert.Equal(expected, TextSearch.Matches(query, "Báo cáo tháng 9.pdf", null));
    }

    [Fact]
    public void Words_may_come_from_different_fields()
    {
        Assert.True(TextSearch.Matches("mai ky thuat", "Lê Thị Mai", "PC-DEV-03", "Phòng Kỹ thuật"));
    }

    [Theory]
    [InlineData("Đỗ Huy", "do huy")]
    [InlineData("Phòng Kế toán", "phong ke toan")]
    [InlineData("ĐẠI", "dai")]
    public void Fold_removes_accents_and_d_stroke(string text, string expected)
    {
        Assert.Equal(expected, TextSearch.Fold(text));
    }
}
