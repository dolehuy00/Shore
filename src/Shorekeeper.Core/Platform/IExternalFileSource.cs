namespace Shorekeeper.Core.Platform;

/// <param name="Name">File name to send it under.</param>
public sealed record ExternalFileInfo(string Name, long Size, DateTimeOffset ModifiedAt);

/// <summary>
/// Files the OS hands over by reference instead of by path, such as Android <c>content://</c> URIs from the share sheet
/// (docs/14-android.md §3.2). Offers keep the location and read it on demand, without copying.
/// </summary>
public interface IExternalFileSource
{
    /// <summary>True for locations this source reads (e.g. starting with <c>content://</c>).</summary>
    bool Owns(string location);

    /// <returns>Null when it can no longer be read (deleted, permission gone).</returns>
    ExternalFileInfo? Describe(string location);

    /// <summary>A seekable stream when the OS allows it, so recipients can resume with Range requests.</summary>
    /// <exception cref="IOException">It can no longer be read.</exception>
    Stream OpenRead(string location);
}

/// <summary>Desktops only send paths.</summary>
public sealed class NullExternalFileSource : IExternalFileSource
{
    public static NullExternalFileSource Instance { get; } = new();

    public bool Owns(string location) => false;

    public ExternalFileInfo? Describe(string location) => null;

    public Stream OpenRead(string location) => throw new IOException($"No source for {location}.");
}
