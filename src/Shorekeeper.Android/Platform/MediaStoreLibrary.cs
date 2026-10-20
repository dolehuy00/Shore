using System.Globalization;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using Android.Provider;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using AndroidUri = Android.Net.Uri;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// "Tự gửi ảnh" (docs/14-android.md §3.7): the phone's pictures and videos through MediaStore. Markers are MediaStore row
/// ids, which grow with each item added. Camera items are those in DCIM/Camera, screenshots those in a "Screenshots"
/// folder; items still being written are left for later. Locations are the items' content URIs, read by
/// <see cref="ContentUriFiles"/>, which reports their size and modification time the same way.
/// </summary>
internal sealed class MediaStoreLibrary : IMediaLibrary
{
    private const string Camera = "relative_path LIKE 'DCIM/Camera/%'";
    private const string Screenshots = "relative_path LIKE '%Screenshots%'";
    private const int Image = 1;
    private const int Video = 3;

    private readonly Context context;
    private readonly ContentResolver resolver;

    public MediaStoreLibrary(Context context)
    {
        this.context = context;
        resolver = context.ContentResolver!;
        resolver.RegisterContentObserver(Files, notifyForDescendants: true, new Observer(this));
    }

    public event EventHandler? Changed;

    public static string[] Permissions =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ? [global::Android.Manifest.Permission.ReadMediaImages, global::Android.Manifest.Permission.ReadMediaVideo]
            : [global::Android.Manifest.Permission.ReadExternalStorage];

    public bool CanRead => Permissions.All(p => context.CheckSelfPermission(p) == Permission.Granted);

    private static AndroidUri Files => MediaStore.Files.GetContentUri("external")!;

    public long LatestMarker()
    {
        using ICursor? cursor = Query(["_id"], $"media_type IN ({Image},{Video})", [], "_id DESC", 1);
        return cursor is not null && cursor.MoveToFirst() ? cursor.GetLong(0) : 0;
    }

    public IReadOnlyList<MediaItem> After(long marker, MediaKinds kinds, int max)
    {
        List<string> wanted = [];
        if (kinds.HasFlag(MediaKinds.CameraPhotos))
        {
            wanted.Add($"(media_type = {Image} AND {Camera})");
        }

        if (kinds.HasFlag(MediaKinds.CameraVideos))
        {
            wanted.Add($"(media_type = {Video} AND {Camera})");
        }

        if (kinds.HasFlag(MediaKinds.Screenshots))
        {
            wanted.Add($"(media_type = {Image} AND {Screenshots})");
        }

        if (kinds.HasFlag(MediaKinds.Other))
        {
            wanted.Add($"(media_type IN ({Image},{Video}) AND NOT {Camera} AND NOT {Screenshots})");
        }

        if (wanted.Count == 0)
        {
            return [];
        }

        string[] columns = ["_id", "media_type", "_display_name", "_size", "date_added", "date_modified"];
        using ICursor? cursor = Query(columns, $"_id > ? AND is_pending = 0 AND ({string.Join(" OR ", wanted)})", [marker.ToString(CultureInfo.InvariantCulture)], "_id ASC", max);
        List<MediaItem> items = [];
        while (cursor is not null && cursor.MoveToNext())
        {
            long id = cursor.GetLong(0);
            AndroidUri collection = cursor.GetInt(1) == Video ? MediaStore.Video.Media.ExternalContentUri! : MediaStore.Images.Media.ExternalContentUri!;
            items.Add(new MediaItem(
                id,
                ContentUris.WithAppendedId(collection, id).ToString()!,
                cursor.GetString(2) ?? $"{id}",
                cursor.GetLong(3),
                DateTimeOffset.FromUnixTimeSeconds(cursor.GetLong(4)),
                DateTimeOffset.FromUnixTimeSeconds(cursor.GetLong(5))));
        }

        return items;
    }

    private ICursor? Query(string[] columns, string selection, string[] arguments, string order, int limit)
    {
        var query = new Bundle();
        query.PutString(ContentResolver.QueryArgSqlSelection, selection);
        query.PutStringArray(ContentResolver.QueryArgSqlSelectionArgs, arguments);
        query.PutString(ContentResolver.QueryArgSqlSortOrder, order);
        query.PutInt(ContentResolver.QueryArgLimit, limit);
        try
        {
            return resolver.Query(Files, columns, query, null);
        }
        catch (Java.Lang.SecurityException)
        {
            // The permission was taken back: nothing to send until it is given again.
            return null;
        }
    }

    private sealed class Observer(MediaStoreLibrary library) : ContentObserver(null)
    {
        public override void OnChange(bool selfChange) => library.Changed?.Invoke(library, EventArgs.Empty);
    }
}

/// <summary>Asking for the permission to read pictures and videos ("Tự gửi ảnh").</summary>
internal static class MediaPermission
{
    private const int Request = 30;
    private static TaskCompletionSource<bool>? asked;

    /// <returns>Whether the app may read the library.</returns>
    /// <exception cref="InvalidOperationException">Refused for good: only the app's settings can grant it now.</exception>
    public static async Task<bool> RequestAsync(Activity activity)
    {
        if (MediaStoreLibrary.Permissions.All(p => activity.CheckSelfPermission(p) == Permission.Granted))
        {
            return true;
        }

        asked = new TaskCompletionSource<bool>();
        activity.RequestPermissions(MediaStoreLibrary.Permissions, Request);
        if (await asked.Task)
        {
            return true;
        }

        throw new InvalidOperationException(L.T("Tự gửi ảnh cần quyền xem ảnh và video. Nếu đã từ chối, bật lại trong Cài đặt → Ứng dụng → Shorekeeper → Quyền."));
    }

    /// <returns>Whether the request was ours.</returns>
    public static bool OnPermissionResult(int requestCode, Permission[] grantResults)
    {
        if (requestCode != Request)
        {
            return false;
        }

        asked?.TrySetResult(grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted));
        return true;
    }
}
