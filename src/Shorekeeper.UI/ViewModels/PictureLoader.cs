using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Shorekeeper.UI.ViewModels;

/// <summary>
/// Pictures shown in the app (docs/19-transfers-preview.md §4): decoded no bigger than they are shown, and turned the way
/// the camera says. A phone stores a portrait photo lying on its side and tags it (EXIF Orientation); the decoder does
/// not read the tag, so without this the photo shows sideways.
/// </summary>
public static class PictureLoader
{
    /// <summary>What the start of a JPEG or PNG says; zero size when it is neither or does not say.</summary>
    /// <param name="Orientation">EXIF 1 to 8; 1 is upright.</param>
    public readonly record struct Header(int Width, int Height, int Orientation)
    {
        /// <summary>Lying on its side: shown, its width is the stored height.</summary>
        public bool Sideways => Orientation >= 5;

        public int ShownWidth => Sideways ? Height : Width;

        public int ShownHeight => Sideways ? Width : Height;
    }

    /// <summary>
    /// Upright, and at most <paramref name="maxWidth"/> × <paramref name="maxHeight"/> as shown; a small picture keeps
    /// its own size. The stream must be readable from the start.
    /// </summary>
    public static Bitmap Decode(Stream stream, int maxWidth, int maxHeight = int.MaxValue)
    {
        Header header = stream.CanSeek ? ReadHeader(stream) : new Header(0, 0, 1);
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        double scale = header.Width > 0 && header.Height > 0
            ? Math.Min(1, Math.Min((double)maxWidth / header.ShownWidth, (double)maxHeight / header.ShownHeight))
            : 1;
        int width = header.Width > 0 ? Math.Max(1, (int)Math.Round(header.ShownWidth * scale)) : maxWidth;

        // Lying on its side, the stored height is the width shown.
        Bitmap bitmap = header.Sideways
            ? Bitmap.DecodeToHeight(stream, width, BitmapInterpolationMode.MediumQuality)
            : Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
        return header.Orientation == 1 ? bitmap : Turn(bitmap, header.Orientation);
    }

    /// <summary>Reads only as far as the size (and, in a JPEG, the orientation before it); leaves the stream anywhere.</summary>
    public static Header ReadHeader(Stream stream)
    {
        Span<byte> start = stackalloc byte[8];
        if (stream.ReadAtLeast(start, 8, throwOnEndOfStream: false) < 8)
        {
            return new Header(0, 0, 1);
        }

        if (start[0] == 0xFF && start[1] == 0xD8)
        {
            stream.Position = 2;
            return ReadJpeg(stream);
        }

        // PNG: the signature, then IHDR with the width and height.
        if (start.SequenceEqual(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            Span<byte> ihdr = stackalloc byte[16];
            return stream.ReadAtLeast(ihdr, 16, throwOnEndOfStream: false) == 16 && ihdr[4..8].SequenceEqual("IHDR"u8)
                ? new Header(BinaryPrimitives.ReadInt32BigEndian(ihdr[8..]), BinaryPrimitives.ReadInt32BigEndian(ihdr[12..]), 1)
                : new Header(0, 0, 1);
        }

        return new Header(0, 0, 1);
    }

    /// <summary>
    /// Where the pixel shown at (<paramref name="x"/>, <paramref name="y"/>) is stored, for a picture stored
    /// <paramref name="width"/> × <paramref name="height"/> with EXIF <paramref name="orientation"/>.
    /// </summary>
    internal static (int X, int Y) StoredAt(int x, int y, int width, int height, int orientation) => orientation switch
    {
        2 => (width - 1 - x, y),
        3 => (width - 1 - x, height - 1 - y),
        4 => (x, height - 1 - y),
        5 => (y, x),
        6 => (y, height - 1 - x),
        7 => (width - 1 - y, height - 1 - x),
        8 => (width - 1 - y, x),
        _ => (x, y),
    };

    /// <summary>The pixels of a stored picture rearranged as shown; 32-bit pixels, row after row.</summary>
    internal static uint[] Turn(uint[] stored, int width, int height, int orientation)
    {
        bool sideways = orientation >= 5;
        int shownWidth = sideways ? height : width;
        int shownHeight = sideways ? width : height;
        var shown = new uint[stored.Length];
        for (int y = 0; y < shownHeight; y++)
        {
            for (int x = 0; x < shownWidth; x++)
            {
                (int sx, int sy) = StoredAt(x, y, width, height, orientation);
                shown[(y * shownWidth) + x] = stored[(sy * width) + sx];
            }
        }

        return shown;
    }

    private static Bitmap Turn(Bitmap bitmap, int orientation)
    {
        if (bitmap.Format is not { BitsPerPixel: 32 } format)
        {
            return bitmap;
        }

        PixelSize size = bitmap.PixelSize;
        var stored = new uint[size.Width * size.Height];
        GCHandle pin = GCHandle.Alloc(stored, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), pin.AddrOfPinnedObject(), stored.Length * 4, size.Width * 4);
        }
        finally
        {
            pin.Free();
        }

        uint[] shown = Turn(stored, size.Width, size.Height, orientation);
        var shownSize = orientation >= 5 ? new PixelSize(size.Height, size.Width) : size;
        pin = GCHandle.Alloc(shown, GCHandleType.Pinned);
        try
        {
            var turned = new Bitmap(format, bitmap.AlphaFormat ?? AlphaFormat.Premul, pin.AddrOfPinnedObject(), shownSize, bitmap.Dpi, shownSize.Width * 4);
            bitmap.Dispose();
            return turned;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Markers until the frame header; the orientation from APP1 "Exif" on the way.</summary>
    private static Header ReadJpeg(Stream stream)
    {
        int orientation = 1;
        Span<byte> marker = stackalloc byte[4];
        while (stream.ReadAtLeast(marker[..2], 2, throwOnEndOfStream: false) == 2)
        {
            if (marker[0] != 0xFF)
            {
                break;
            }

            byte code = marker[1];
            if (code == 0xFF)
            {
                // Fill byte: the marker follows.
                stream.Position--;
                continue;
            }

            if (code is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (code is 0xD9 or 0xDA || stream.ReadAtLeast(marker[2..], 2, throwOnEndOfStream: false) < 2)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(marker[2..]) - 2;
            if (length < 0)
            {
                break;
            }

            bool frame = code is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (frame || code == 0xE1)
            {
                byte[] payload = new byte[length];
                if (stream.ReadAtLeast(payload, length, throwOnEndOfStream: false) < length)
                {
                    break;
                }

                if (frame)
                {
                    return length >= 5
                        ? new Header(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(3)), BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(1)), orientation)
                        : new Header(0, 0, orientation);
                }

                orientation = ExifOrientation(payload) ?? orientation;
            }
            else
            {
                stream.Seek(length, SeekOrigin.Current);
            }
        }

        return new Header(0, 0, orientation);
    }

    /// <summary>Tag 0x0112 of the first IFD in an APP1 "Exif" segment, when it is there and makes sense.</summary>
    private static int? ExifOrientation(ReadOnlySpan<byte> app1)
    {
        if (app1.Length < 14 || !app1[..6].SequenceEqual("Exif\0\0"u8))
        {
            return null;
        }

        ReadOnlySpan<byte> tiff = app1[6..];
        bool little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!little && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M'))
        {
            return null;
        }

        int ifd = (int)Read32(tiff[4..]);
        if (ifd < 8 || ifd + 2 > tiff.Length)
        {
            return null;
        }

        int count = Read16(tiff[ifd..]);
        for (int i = 0; i < count; i++)
        {
            int entry = ifd + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                return null;
            }

            if (Read16(tiff[entry..]) == 0x0112)
            {
                int value = Read16(tiff[(entry + 8)..]);
                return value is >= 1 and <= 8 ? value : null;
            }
        }

        return null;

        int Read16(ReadOnlySpan<byte> at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(at) : BinaryPrimitives.ReadUInt16BigEndian(at);

        uint Read32(ReadOnlySpan<byte> at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(at) : BinaryPrimitives.ReadUInt32BigEndian(at);
    }
}
