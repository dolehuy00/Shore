using Android.Content;
using Android.Provider;
using Microsoft.Win32.SafeHandles;
using Shorekeeper.Core.Platform;
using AndroidUri = Android.Net.Uri;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// Shared items and picked files arrive as <c>content://</c> URIs. They are read in place through a file descriptor,
/// which can seek, so recipients can resume with Range requests (docs/14-android.md §3.2).
/// </summary>
public sealed class ContentUriFiles(ContentResolver resolver) : IExternalFileSource
{
    public bool Owns(string location) => location.StartsWith("content://", StringComparison.Ordinal);

    /// <summary>
    /// Keeps reading a picked document after the app restarts (its picture still shows in Tin nhắn); the picker's grant
    /// otherwise ends with the app. Android drops the oldest kept grants once there are too many.
    /// </summary>
    public static void Keep(ContentResolver resolver, string location)
    {
        try
        {
            resolver.TakePersistableUriPermission(AndroidUri.Parse(location)!, ActivityFlags.GrantReadUriPermission);
        }
        catch (Java.Lang.SecurityException)
        {
            // Not offered as lasting (shared from another app, or a provider without it): readable while the grant lasts.
        }
    }

    public ExternalFileInfo? Describe(string location)
    {
        try
        {
            using var cursor = resolver.Query(AndroidUri.Parse(location)!, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
            {
                return null;
            }

            string name = Text(IOpenableColumns.DisplayName) ?? "file";
            long size = Number(IOpenableColumns.Size) ?? SizeFromDescriptor(location);
            // Documents give milliseconds, MediaStore gives seconds; without either the time is fixed, so the ETag stays stable.
            DateTimeOffset modified = Number(DocumentsContract.Document.ColumnLastModified) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : Number("date_modified") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.UnixEpoch;
            return new ExternalFileInfo(name, size, modified);

            string? Text(string column) => cursor.GetColumnIndex(column) is >= 0 and var i && !cursor.IsNull(i) ? cursor.GetString(i) : null;

            long? Number(string column) => cursor.GetColumnIndex(column) is >= 0 and var i && !cursor.IsNull(i) ? cursor.GetLong(i) : null;
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.IO.IOException or Java.Lang.IllegalArgumentException)
        {
            // Permission gone (the sharing app revoked it, or the phone restarted) or the item was deleted.
            return null;
        }
    }

    public Stream OpenRead(string location)
    {
        try
        {
            using var descriptor = resolver.OpenFileDescriptor(AndroidUri.Parse(location)!, "r")
                ?? throw new IOException($"Cannot open {location}.");
            return new FileStream(new SafeFileHandle(descriptor.DetachFd(), ownsHandle: true), FileAccess.Read, 1024 * 1024);
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.IO.IOException or Java.Lang.IllegalArgumentException)
        {
            throw new IOException($"Cannot read {location}: {ex.Message}", ex);
        }
    }

    private long SizeFromDescriptor(string location)
    {
        using var descriptor = resolver.OpenFileDescriptor(AndroidUri.Parse(location)!, "r");
        return descriptor?.StatSize ?? 0;
    }
}
