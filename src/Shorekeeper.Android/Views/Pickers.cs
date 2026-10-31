using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Shorekeeper.Android.Platform;

namespace Shorekeeper.Android.Views;

/// <summary>Android's file picker, for the pages that send files or restore a backup.</summary>
internal static class Pickers
{
    /// <returns>Paths, or content:// URIs kept exactly as given (their document ids are percent-encoded).</returns>
    public static async Task<IReadOnlyList<string>> PickAsync(Visual from, string title, bool allowMultiple)
    {
        if (TopLevel.GetTopLevel(from) is not { } top)
        {
            return [];
        }

        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = title, AllowMultiple = allowMultiple });
        string[] locations = [.. picked.Select(f => f.Path.IsFile ? f.Path.LocalPath : f.Path.OriginalString)];
        if (MainActivity.Current?.ContentResolver is { } resolver)
        {
            foreach (string location in locations.Where(l => l.StartsWith("content://", StringComparison.Ordinal)))
            {
                ContentUriFiles.Keep(resolver, location);
            }
        }

        return locations;
    }
}
