namespace Shorekeeper.Engine;

/// <summary>
/// File-system locations used by the app. See docs/09-data-storage.md §1.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory, string defaultDownloadDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        DefaultDownloadDirectory = Path.GetFullPath(defaultDownloadDirectory);
    }

    /// <summary><c>%LOCALAPPDATA%\Shorekeeper</c></summary>
    public string DataDirectory { get; }

    /// <summary><c>%USERPROFILE%\Downloads\Shorekeeper</c> unless the user or IT changes it.</summary>
    public string DefaultDownloadDirectory { get; }

    public string IdentityFile => Path.Combine(DataDirectory, "identity.pfx.dpapi");

    public string DatabaseFile => Path.Combine(DataDirectory, "shorekeeper.db");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>A restored backup waiting for the next start (encrypted like the identity).</summary>
    public string PendingRestoreFile => Path.Combine(DataDirectory, "restore.pending");

    /// <summary>Last profile read from the company directory (AD), for when it cannot be reached.</summary>
    public string DirectoryCacheFile => Path.Combine(DataDirectory, "directory.json");

    /// <summary>The user's picture from the company directory.</summary>
    public string AvatarFile => Path.Combine(DataDirectory, "avatar.img");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string SnapshotsDirectory => Path.Combine(DataDirectory, "snapshots");

    public static AppPaths ForCurrentUser()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new AppPaths(
            Path.Combine(localAppData, "Shorekeeper"),
            Path.Combine(userProfile, "Downloads", "Shorekeeper"));
    }

    public void EnsureCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DataDirectory);
        }
        else
        {
            // Owner only: outside Windows the device key is protected by these permissions alone.
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(DataDirectory, ownerOnly);
            File.SetUnixFileMode(DataDirectory, ownerOnly);
        }

        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SnapshotsDirectory);
    }
}
