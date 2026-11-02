using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// The small copy of a picture we offer (docs/19-transfers-preview.md §7): upright like everything the app shows, and a
/// JPEG, tens of KB where a PNG would be hundreds. What is see-through shows over white, as JPEG has no transparency.
/// </summary>
public sealed class PictureThumbnailMaker : IThumbnailMaker
{
    private const int Quality = 80;

    public byte[]? Make(Stream picture, int maxSize)
    {
        try
        {
            // The size and orientation are read from the start, then the stream is read again.
            using Stream seekable = picture.CanSeek ? picture : Buffered(picture);
            using Bitmap bitmap = Opaque(PictureLoader.Decode(seekable, maxSize, maxSize));
            using var jpeg = new MemoryStream();
            bitmap.Save(jpeg, new JpegBitmapEncoderOptions { Quality = Quality });
            return jpeg.ToArray();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static MemoryStream Buffered(Stream picture)
    {
        var copy = new MemoryStream();
        picture.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    /// <summary>The picture over white when any of it is see-through; otherwise as it is.</summary>
    private static Bitmap Opaque(Bitmap bitmap)
    {
        if (bitmap.Format is not { BitsPerPixel: 32 } format || bitmap.AlphaFormat is null or AlphaFormat.Opaque)
        {
            return bitmap;
        }

        PixelSize size = bitmap.PixelSize;
        var pixels = new uint[size.Width * size.Height];
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), pin.AddrOfPinnedObject(), pixels.Length * 4, size.Width * 4);
            if (pixels.All(p => p >> 24 == 0xFF))
            {
                return bitmap;
            }

            bool premultiplied = bitmap.AlphaFormat == AlphaFormat.Premul;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = OverWhite(pixels[i], premultiplied);
            }

            var flat = new Bitmap(format, AlphaFormat.Premul, pin.AddrOfPinnedObject(), size, bitmap.Dpi, size.Width * 4);
            bitmap.Dispose();
            return flat;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>One pixel over white; the color channels are the low three bytes in either order, alpha the high one.</summary>
    internal static uint OverWhite(uint pixel, bool premultiplied)
    {
        uint alpha = pixel >> 24;
        uint result = 0xFF000000;
        for (int shift = 0; shift < 24; shift += 8)
        {
            uint channel = (pixel >> shift) & 0xFF;
            uint colored = premultiplied ? channel : (channel * alpha + 127) / 255;
            result |= Math.Min(255, colored + (255 - alpha)) << shift;
        }

        return result;
    }
}
