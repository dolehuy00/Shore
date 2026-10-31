using Shorekeeper.UI.ViewModels;

namespace Shorekeeper.UI.Tests;

public class TransferTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 15, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 31)));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1_572_864, "1,5 MB")]
    [InlineData(3L << 30, "3,0 GB")]
    public void Sizes_are_written_the_Vietnamese_way(long bytes, string expected) => Assert.Equal(expected, TransferText.Size(bytes));

    [Fact]
    public void An_offer_is_summed_up_by_its_top_level_entries()
    {
        Assert.Equal("app.zip", TransferText.Summary(["app.zip"]));
        Assert.Equal("project (thư mục)", TransferText.Summary(["project/", "project/a.txt", "project/b/c.txt"]));
        Assert.Equal("app.zip + 2 mục", TransferText.Summary(["app.zip", "notes.txt", "project/", "project/a.txt"]));
        Assert.Equal("(trống)", TransferText.Summary([]));
    }

    [Fact]
    public void Top_level_folders_count_the_files_in_them()
    {
        Assert.Equal(
            ["project (thư mục · 2 file)", "readme.md"],
            TransferText.TopLevel(["project/", "project/a.txt", "project/b/c.txt", "readme.md"]));
        Assert.Equal(["empty (thư mục · 0 file)"], TransferText.TopLevel(["empty/"]));
    }

    [Fact]
    public void Links_are_the_web_addresses_in_a_message_without_trailing_punctuation()
    {
        Assert.Equal(
            ["https://example.com/a", "http://lan.local:8080/x?y=1"],
            TransferText.LinksIn("Xem https://example.com/a, rồi (http://lan.local:8080/x?y=1). Lại https://example.com/a!"));
        Assert.Empty(TransferText.LinksIn("ftp://example.com và example.com không phải link"));
        Assert.Equal(3, TransferText.LinksIn("https://a.vn https://b.vn https://c.vn https://d.vn").Count);
    }

    [Fact]
    public void Only_a_whole_web_address_is_a_link_to_open()
    {
        Assert.Equal(new Uri("https://example.com/a"), TransferText.LinkOf("  https://example.com/a \n"));
        Assert.Null(TransferText.LinkOf("https://example.com/a và thêm chữ"));
        Assert.Null(TransferText.LinkOf("file:///C:/Windows/system32"));
        Assert.Null(TransferText.LinkOf(null));
    }

    [Fact]
    public void A_preview_is_the_first_line_shortened()
    {
        Assert.Equal("Dòng đầu", TransferText.Preview("  Dòng đầu  \nDòng hai"));
        Assert.Equal("abcd…", TransferText.Preview("abcdefgh", maxLength: 5));
    }

    [Fact]
    public void Times_left_and_ago_read_as_people_say_them()
    {
        Assert.Equal("hết hạn", TransferText.Remaining(TimeSpan.Zero));
        Assert.Equal("còn 2 giờ", TransferText.Remaining(TimeSpan.FromMinutes(150)));
        Assert.Equal("còn 5 phút", TransferText.Remaining(TimeSpan.FromMinutes(5.5)));
        Assert.Equal("sắp hết hạn", TransferText.Remaining(TimeSpan.FromSeconds(30)));

        Assert.Equal("vừa xong", TransferText.Ago(Now.AddSeconds(-20), Now));
        Assert.Equal("5 phút trước", TransferText.Ago(Now.AddMinutes(-5), Now));
        Assert.Equal("3 giờ trước", TransferText.Ago(Now.AddHours(-3), Now));
        Assert.Equal("hôm qua", TransferText.Ago(Now.AddHours(-30), Now));
        Assert.Equal("20/10", TransferText.Ago(Now.AddDays(-11), Now));
    }

    [Fact]
    public void Days_are_grouped_by_the_local_day()
    {
        Assert.Equal("Hôm nay", TransferText.DayGroup(Now.AddHours(-14), Now));
        Assert.Equal("Hôm qua", TransferText.DayGroup(Now.AddHours(-16), Now));
        Assert.Equal("7 ngày qua", TransferText.DayGroup(Now.AddDays(-6), Now));
        Assert.Equal("Trước đó", TransferText.DayGroup(Now.AddDays(-7), Now));
    }

    [Fact]
    public void What_an_offer_mostly_holds_gives_its_kind()
    {
        Assert.Equal(TransferKind.Folder, TransferText.KindOf(["photos/", "photos/a.jpg"]));
        Assert.Equal(TransferKind.Images, TransferText.KindOf(["a.JPG", "b.png"]));
        Assert.Equal(TransferKind.Files, TransferText.KindOf(["a.jpg", "report.pdf"]));
        Assert.Equal(TransferKind.Files, TransferText.KindOf([]));
    }

    [Fact]
    public void Files_gone_from_the_disk_are_told()
    {
        Assert.Null(TransferText.Gone((3, 3)));
        Assert.Equal("Đã xóa file", TransferText.Gone((0, 3)));
        Assert.Equal("Còn 2/3 file", TransferText.Gone((2, 3)));
    }

    [Fact]
    public void The_view_keeps_what_the_filter_and_search_ask_with_a_heading_per_day()
    {
        Row[] rows =
        [
            new(Now.AddHours(-1), TransferKind.Images, "Mai", "anh-bien.jpg"),
            new(Now.AddHours(-2), TransferKind.Files, "Tuấn", "báo cáo.pdf"),
            new(Now.AddHours(-3), TransferKind.Images, "Tuấn", "chụp màn hình.png"),
            new(Now.AddDays(-1), TransferKind.Images, "Mai", "hôm qua.png"),
        ];

        TransferText.ApplyView(rows, FileFilter.Images, "", Now);
        Assert.Equal([true, false, true, true], rows.Select(r => r.IsShown));
        Assert.Equal(["Hôm nay", null, null, "Hôm qua"], rows.Select(r => r.DayText));

        // All words, with or without accents, as Ctrl+K.
        TransferText.ApplyView(rows, FileFilter.All, "tuan bao", Now);
        Assert.Equal([false, true, false, false], rows.Select(r => r.IsShown));
        Assert.Equal([null, "Hôm nay", null, null], rows.Select(r => r.DayText));
    }

    private sealed class Row(DateTimeOffset at, TransferKind kind, params string[] fields) : IFileRow
    {
        public DateTimeOffset At => at;

        public TransferKind Kind => kind;

        public IEnumerable<string> SearchFields => fields;

        public bool IsShown { get; set; }

        public string? DayText { get; set; }
    }
}
