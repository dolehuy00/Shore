using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Monitoring;

/// <summary>Kinds of received files, for "Thống kê" → the download folder.</summary>
public enum StorageKind
{
    Pictures,
    Videos,
    Audio,
    Documents,
    Archives,
    Other,
}

public sealed record StorageKindUsage(StorageKind Kind, long Bytes, int Files);

/// <param name="DriveTotal">Size of the drive holding the download folder; 0 when it cannot be read.</param>
/// <param name="DriveFree">Free space there, as available to this app.</param>
/// <param name="Kinds">Only kinds with files, largest first.</param>
public sealed record StorageReport(long DriveTotal, long DriveFree, long FolderBytes, int FolderFiles, IReadOnlyList<StorageKindUsage> Kinds)
{
    private static readonly Dictionary<string, StorageKind> KindByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = StorageKind.Pictures, [".jpeg"] = StorageKind.Pictures, [".png"] = StorageKind.Pictures, [".gif"] = StorageKind.Pictures,
        [".bmp"] = StorageKind.Pictures, [".webp"] = StorageKind.Pictures, [".heic"] = StorageKind.Pictures, [".heif"] = StorageKind.Pictures,
        [".mp4"] = StorageKind.Videos, [".mov"] = StorageKind.Videos, [".mkv"] = StorageKind.Videos, [".avi"] = StorageKind.Videos,
        [".webm"] = StorageKind.Videos, [".3gp"] = StorageKind.Videos, [".wmv"] = StorageKind.Videos,
        [".mp3"] = StorageKind.Audio, [".m4a"] = StorageKind.Audio, [".wav"] = StorageKind.Audio, [".flac"] = StorageKind.Audio,
        [".aac"] = StorageKind.Audio, [".ogg"] = StorageKind.Audio, [".opus"] = StorageKind.Audio,
        [".pdf"] = StorageKind.Documents, [".doc"] = StorageKind.Documents, [".docx"] = StorageKind.Documents, [".xls"] = StorageKind.Documents,
        [".xlsx"] = StorageKind.Documents, [".ppt"] = StorageKind.Documents, [".pptx"] = StorageKind.Documents, [".txt"] = StorageKind.Documents,
        [".md"] = StorageKind.Documents, [".csv"] = StorageKind.Documents, [".odt"] = StorageKind.Documents, [".rtf"] = StorageKind.Documents,
        [".zip"] = StorageKind.Archives, [".rar"] = StorageKind.Archives, [".7z"] = StorageKind.Archives, [".tar"] = StorageKind.Archives,
        [".gz"] = StorageKind.Archives, [".apk"] = StorageKind.Archives, [".exe"] = StorageKind.Archives, [".msi"] = StorageKind.Archives,
    };

    /// <summary>
    /// Reads the drive and walks <paramref name="folder"/> (links not followed, unreadable folders skipped). Can take a
    /// while on a large folder: call it off the UI thread.
    /// </summary>
    public static StorageReport Measure(string folder)
    {
        long total = 0;
        long free = 0;
        try
        {
            DriveInfo drive = DiskSpace.DriveOf(folder);
            (total, free) = (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        Dictionary<StorageKind, (long Bytes, int Files)> kinds = [];
        if (Directory.Exists(folder))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                StorageKind kind = KindByExtension.GetValueOrDefault(file.Extension, StorageKind.Other);
                (long bytes, int files) = kinds.GetValueOrDefault(kind);
                kinds[kind] = (bytes + file.Length, files + 1);
            }
        }

        return new StorageReport(
            total, free, kinds.Values.Sum(k => k.Bytes), kinds.Values.Sum(k => k.Files),
            [.. kinds.Select(k => new StorageKindUsage(k.Key, k.Value.Bytes, k.Value.Files)).OrderByDescending(k => k.Bytes)]);
    }
}
