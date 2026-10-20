namespace Shorekeeper.Core.Platform;

/// <summary>What "Tự gửi ảnh" sends, as the user ticks it.</summary>
[Flags]
public enum MediaKinds
{
    None = 0,

    /// <summary>Taken with the camera (DCIM/Camera).</summary>
    CameraPhotos = 1,
    CameraVideos = 2,
    Screenshots = 4,

    /// <summary>Everything else: downloads, chat apps' pictures…</summary>
    Other = 8,
}

/// <summary>A picture or video in the library.</summary>
/// <param name="Marker">Grows with each item added: "new" means after the marker last sent.</param>
/// <param name="Location">What <see cref="IExternalFileSource"/> reads, e.g. a content:// URI, or a path.</param>
/// <param name="ModifiedAt">As the source reports it when the file is served: a different time reads as a changed file.</param>
public sealed record MediaItem(long Marker, string Location, string Name, long Size, DateTimeOffset AddedAt, DateTimeOffset ModifiedAt);

/// <summary>The phone's photos and videos, for "Tự gửi ảnh" (Android).</summary>
public interface IMediaLibrary
{
    /// <summary>The app may read the library (the permission was given).</summary>
    bool CanRead { get; }

    /// <summary>A picture or video was added or removed. Raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>The marker of the newest item now: what is added later is new.</summary>
    long LatestMarker();

    /// <summary>Items of <paramref name="kinds"/> added after <paramref name="marker"/>, oldest first, at most <paramref name="max"/>.</summary>
    IReadOnlyList<MediaItem> After(long marker, MediaKinds kinds, int max);
}

/// <summary>The kinds' names in settings ("photos,videos,screenshots,other").</summary>
public static class MediaKindNames
{
    public const MediaKinds Default = MediaKinds.CameraPhotos | MediaKinds.CameraVideos;

    private static readonly (MediaKinds Kind, string Name)[] Names =
    [
        (MediaKinds.CameraPhotos, "photos"),
        (MediaKinds.CameraVideos, "videos"),
        (MediaKinds.Screenshots, "screenshots"),
        (MediaKinds.Other, "other"),
    ];

    /// <returns><see cref="Default"/> when <paramref name="text"/> is null.</returns>
    public static MediaKinds Parse(string? text) =>
        text is null ? Default
        : Names.Where(n => text.Split(',').Contains(n.Name, StringComparer.OrdinalIgnoreCase)).Aggregate(MediaKinds.None, (all, n) => all | n.Kind);

    public static string Format(MediaKinds kinds) => string.Join(',', Names.Where(n => kinds.HasFlag(n.Kind)).Select(n => n.Name));
}
