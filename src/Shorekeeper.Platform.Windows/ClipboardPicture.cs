using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// Puts a picture on the Windows clipboard the way screenshot tools do: CF_DIB, which chat apps, Paint and Word paste
/// as a picture, and "PNG", which browsers and Office prefer (it keeps transparency). Avalonia's own bitmap copy was
/// pasted by Zalo as an invalid 0 KB file.
/// </summary>
public static partial class ClipboardPicture
{
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;
    private const int HeaderSize = 40;

    /// <param name="bgra">Pixels top row first, 4 bytes each (blue, green, red, alpha), no padding.</param>
    /// <param name="png">The same picture as a PNG file.</param>
    /// <returns>False when the clipboard stayed busy (another app holds it).</returns>
    public static bool Set(int width, int height, ReadOnlySpan<byte> bgra, ReadOnlySpan<byte> png)
    {
        // Another app may hold the clipboard for a moment.
        bool opened = false;
        for (int attempt = 0; attempt < 10 && !(opened = OpenClipboard(0)); attempt++)
        {
            Thread.Sleep(20);
        }

        if (!opened)
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            return Put(CfDib, Dib(width, height, bgra)) & Put(RegisterClipboardFormatW("PNG"), png);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>A BITMAPINFOHEADER and the rows bottom-up, as a DIB is laid out.</summary>
    private static byte[] Dib(int width, int height, ReadOnlySpan<byte> bgra)
    {
        int stride = width * 4;
        byte[] dib = new byte[HeaderSize + (stride * height)];
        Span<byte> header = dib.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);
        for (int row = 0; row < height; row++)
        {
            bgra.Slice(row * stride, stride).CopyTo(dib.AsSpan(HeaderSize + ((height - 1 - row) * stride), stride));
        }

        return dib;
    }

    private static unsafe bool Put(uint format, ReadOnlySpan<byte> data)
    {
        nint memory = GlobalAlloc(GmemMoveable, (nuint)data.Length);
        if (memory == 0)
        {
            return false;
        }

        void* target = (void*)GlobalLock(memory);
        data.CopyTo(new Span<byte>(target, data.Length));
        GlobalUnlock(memory);
        // Once set, the memory belongs to the clipboard; only a refused one is ours to free.
        if (SetClipboardData(format, memory) == 0)
        {
            GlobalFree(memory);
            return false;
        }

        return true;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
}
