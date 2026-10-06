using System.Xml.Linq;

namespace Shorekeeper.Platform.Windows.Tests;

public class WindowsToastNotifierTests
{
    [Fact]
    public void Toast_has_title_lines_and_numbered_buttons()
    {
        XElement toast = XElement.Parse(WindowsToastNotifier.BuildXml("Huy gửi cho bạn", ["app.zip · 2.4 GB", "\"Bản build\""], ["Tải", "Bỏ qua", "Xem"]));

        Assert.Equal("-1", toast.Attribute("launch")?.Value);
        Assert.Equal(["Huy gửi cho bạn", "app.zip · 2.4 GB", "\"Bản build\""], toast.Descendants("text").Select(t => t.Value));
        Assert.Equal(["0", "1", "2"], toast.Descendants("action").Select(a => a.Attribute("arguments")?.Value));
        Assert.Equal(["Tải", "Bỏ qua", "Xem"], toast.Descendants("action").Select(a => a.Attribute("content")?.Value));
    }

    [Fact]
    public void Names_cannot_break_the_xml()
    {
        // Peer names and notes come from other computers.
        string xml = WindowsToastNotifier.BuildXml("<b>&\"", ["</text><action/>"], []);

        XElement toast = XElement.Parse(xml);
        Assert.Equal(["<b>&\"", "</text><action/>"], toast.Descendants("text").Select(t => t.Value));
        Assert.Empty(toast.Descendants("action"));
    }

    [Fact]
    public void At_most_two_lines_below_the_title()
    {
        XElement toast = XElement.Parse(WindowsToastNotifier.BuildXml("A", ["1", "2", "3"], []));

        Assert.Equal(3, toast.Descendants("text").Count());
    }
}
