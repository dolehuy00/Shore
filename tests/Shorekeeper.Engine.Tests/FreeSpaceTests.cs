using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Tests;

/// <summary>Free space is checked on the volume that really holds the download folder.</summary>
public class FreeSpaceTests
{
    [Fact]
    public void Android_downloads_are_on_the_shared_storage_not_on_root()
    {
        string[] mounts = ["/", "/data", "/storage/emulated", "/storage/emulated/0/Android/obb"];

        Assert.Equal("/storage/emulated", DiskSpace.MountPointOf("/storage/emulated/0/Download/Shorekeeper", mounts));
        Assert.Equal("/", DiskSpace.MountPointOf("/system/app", mounts));
    }

    [Fact]
    public void A_mount_only_matches_whole_path_segments()
    {
        Assert.Equal("/", DiskSpace.MountPointOf("/data2/files", ["/", "/data"]));
        Assert.Equal("/data", DiskSpace.MountPointOf("/data", ["/", "/data"]));
    }

    [Fact]
    public void Windows_drive_letters_still_work()
    {
        Assert.Equal(@"D:\", DiskSpace.MountPointOf(@"D:\Nhan\Shorekeeper", [@"C:\", @"D:\"]));
    }
}
