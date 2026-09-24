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
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SnapshotsDirectory);
    }
}
