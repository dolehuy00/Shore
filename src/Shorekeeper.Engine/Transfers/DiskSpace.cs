namespace Shorekeeper.Engine.Transfers;

/// <summary>The volume holding a folder, e.g. the download folder: free space before a download, the "this device" card.</summary>
public static class DiskSpace
{
    public static DriveInfo DriveOf(string path)
    {
        string full = Path.GetFullPath(path);
        DriveInfo[] drives = DriveInfo.GetDrives();
        return MountPointOf(full, drives.Select(d => d.Name)) is { } mount
            ? drives.First(d => d.Name == mount)
            : new DriveInfo(Path.GetPathRoot(full)!);
    }

    /// <summary>
    /// The mount point holding <paramref name="path"/>: the longest one it lies under. On Linux and Android the path's
    /// root ("/") is usually a small system volume, not where Downloads live.
    /// </summary>
    internal static string? MountPointOf(string path, IEnumerable<string> mountPoints)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return mountPoints.Where(Holds).MaxBy(mount => mount.Length);

        // "/" (trimmed to "") holds everything; otherwise the path must continue with a separator after the mount point.
        bool Holds(string mount)
        {
            string trimmed = mount.TrimEnd('/', '\\');
            return path.StartsWith(trimmed, comparison) && (path.Length == trimmed.Length || path[trimmed.Length] is '/' or '\\');
        }
    }
}
