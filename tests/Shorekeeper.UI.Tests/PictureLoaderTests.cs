using Shorekeeper.UI.ViewModels;

namespace Shorekeeper.UI.Tests;

public class PictureLoaderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_photo_says_its_size_and_which_way_is_up(bool littleEndian)
    {
        byte[] jpeg = Jpeg(width: 4000, height: 3000, orientation: 6, littleEndian);

        PictureLoader.Header header = PictureLoader.ReadHeader(new MemoryStream(jpeg));

        Assert.Equal((4000, 3000, 6), (header.Width, header.Height, header.Orientation));
        Assert.True(header.Sideways);
        Assert.Equal((3000, 4000), (header.ShownWidth, header.ShownHeight));
    }

    [Fact]
    public void Without_a_tag_or_a_known_kind_a_picture_is_upright()
    {
        Assert.Equal(new PictureLoader.Header(640, 480, 1), PictureLoader.ReadHeader(new MemoryStream(Jpeg(640, 480, orientation: null, littleEndian: true))));
        Assert.Equal(new PictureLoader.Header(0, 0, 1), PictureLoader.ReadHeader(new MemoryStream("GIF89a\0\0\0\0"u8.ToArray())));
        Assert.Equal(new PictureLoader.Header(0, 0, 1), PictureLoader.ReadHeader(new MemoryStream([0xFF, 0xD8])));

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x02, 0x80, 0, 0, 0x01, 0xE0];
        Assert.Equal(new PictureLoader.Header(640, 480, 1), PictureLoader.ReadHeader(new MemoryStream(png)));
    }

    [Fact]
    public void A_picture_is_turned_the_way_its_tag_says()
    {
        // Stored 3 wide, 2 high:  1 2 3 / 4 5 6.
        uint[] stored = [1, 2, 3, 4, 5, 6];

        Assert.Equal(stored, PictureLoader.Turn(stored, 3, 2, 1));
        Assert.Equal([3u, 2, 1, 6, 5, 4], PictureLoader.Turn(stored, 3, 2, 2));
        Assert.Equal([6u, 5, 4, 3, 2, 1], PictureLoader.Turn(stored, 3, 2, 3));
        Assert.Equal([4u, 5, 6, 1, 2, 3], PictureLoader.Turn(stored, 3, 2, 4));
        // Sideways: shown 2 wide, 3 high. 6 turns it a quarter clockwise, 8 a quarter back.
        Assert.Equal([1u, 4, 2, 5, 3, 6], PictureLoader.Turn(stored, 3, 2, 5));
        Assert.Equal([4u, 1, 5, 2, 6, 3], PictureLoader.Turn(stored, 3, 2, 6));
        Assert.Equal([6u, 3, 5, 2, 4, 1], PictureLoader.Turn(stored, 3, 2, 7));
        Assert.Equal([3u, 6, 2, 5, 1, 4], PictureLoader.Turn(stored, 3, 2, 8));
    }

    [Fact]
    public void A_see_through_picture_turns_white_where_nothing_shows()
    {
        Assert.Equal(0xFFFFFFFFu, PictureThumbnailMaker.OverWhite(0x00000000, premultiplied: true));
        Assert.Equal(0xFF102030u, PictureThumbnailMaker.OverWhite(0xFF102030, premultiplied: true));
        // Half see-through red: premultiplied it holds half its red already; unpremultiplied all of it.
        Assert.Equal(0xFFFF7F7Fu, PictureThumbnailMaker.OverWhite(0x80800000, premultiplied: true));
        Assert.Equal(0xFFFF7F7Fu, PictureThumbnailMaker.OverWhite(0x80FF0000, premultiplied: false));
    }

    /// <summary>The start of a JPEG: an APP1 "Exif" segment with the orientation (when given), then the frame header.</summary>
    private static byte[] Jpeg(int width, int height, int? orientation, bool littleEndian)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        // Something before, to be skipped: APP0 "JFIF".
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x07, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00]);

        if (orientation is { } value)
        {
            var tiff = new List<byte>();
            tiff.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
            tiff.AddRange(U16(42));
            tiff.AddRange(U32(8));
            tiff.AddRange(U16(2));
            // A tag before the one wanted: ImageWidth.
            tiff.AddRange([.. U16(0x0100), .. U16(3), .. U32(1), .. U16((ushort)width), 0, 0]);
            tiff.AddRange([.. U16(0x0112), .. U16(3), .. U32(1), .. U16((ushort)value), 0, 0]);
            tiff.AddRange(U32(0));
            byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. tiff];
            bytes.AddRange([0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            bytes.AddRange(payload);
        }

        // SOF0: precision, height, width, components.
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x0B, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]);
        bytes.AddRange([0xFF, 0xDA]);
        return [.. bytes];

        byte[] U16(ushort v) => littleEndian ? [(byte)v, (byte)(v >> 8)] : [(byte)(v >> 8), (byte)v];

        byte[] U32(uint v) => littleEndian
            ? [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]
            : [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }
}
