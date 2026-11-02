namespace Shorekeeper.Core.Platform;

/// <summary>
/// The small copy of a picture we offer, which a recipient sees in the conversation before downloading it
/// (docs/19-transfers-preview.md §7).
/// </summary>
public interface IThumbnailMaker
{
    /// <returns>An upright JPEG at most <paramref name="maxSize"/> on its longer side; null when the picture cannot be read.</returns>
    byte[]? Make(Stream picture, int maxSize);
}

public sealed class NullThumbnailMaker : IThumbnailMaker
{
    public static NullThumbnailMaker Instance { get; } = new();

    public byte[]? Make(Stream picture, int maxSize) => null;
}
