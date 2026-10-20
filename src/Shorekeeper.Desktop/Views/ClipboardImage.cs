using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Settings;
using Shorekeeper.Platform.Windows;

namespace Shorekeeper.Desktop.Views;

/// <summary>
/// A picture copied as a picture (Win+Shift+S, "Copy image" in a browser), not as a file (docs/10-ux.md §2): sending
/// needs a file, so it is saved as a PNG in the app's data. The file must outlive any offer of it, so files older than
/// the longest offer are cleared when a new one is saved.
/// </summary>
internal static class ClipboardImage
{
    /// <returns>The PNG written, or null when the clipboard holds no picture.</returns>
    public static async Task<string?> SaveAsync(IClipboard clipboard, string directory)
    {
        using Bitmap? bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null)
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        DeleteOld(directory);
        string path = Path.Combine(directory, $"{L.F("Ảnh {0}", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture))}.png");
        for (int i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)} ({i}).png");
        }

        await using (FileStream file = File.Create(path))
        {
            bitmap.Save(file, PngBitmapEncoderOptions.Default);
        }

        return path;
    }

    /// <summary>
    /// Puts a received picture on the clipboard as a picture, to paste into a chat or a document: its pixels as a DIB
    /// and the file as a PNG (<see cref="ClipboardPicture"/>).
    /// </summary>
    /// <returns>False when it cannot be read (deleted, not really a picture) or the clipboard stayed busy.</returns>
    public static bool Copy(string path)
    {
        try
        {
            using var bitmap = new Bitmap(path);
            PixelSize size = bitmap.PixelSize;
            byte[] pixels = new byte[size.Width * size.Height * 4];
            GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), pinned.AddrOfPinnedObject(), pixels.Length, size.Width * 4);
            }
            finally
            {
                pinned.Free();
            }

            if (bitmap.Format == PixelFormats.Rgba8888)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                }
            }

            byte[] png;
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                png = File.ReadAllBytes(path);
            }
            else
            {
                using var encoded = new MemoryStream();
                bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
                png = encoded.ToArray();
            }

            return ClipboardPicture.Set(size.Width, size.Height, pixels, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void DeleteOld(string directory)
    {
        DateTime before = DateTime.UtcNow - EffectiveSettings.MaxOfferLifetime;
        foreach (string old in Directory.EnumerateFiles(directory, "*.png").Where(f => File.GetLastWriteTimeUtc(f) < before))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still being read by a download: next time.
            }
        }
    }
}
