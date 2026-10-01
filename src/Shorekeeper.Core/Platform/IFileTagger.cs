namespace Shorekeeper.Core.Platform;

/// <summary>
/// Marks received files as coming from another computer, so the OS treats them with care
/// (SmartScreen, Office Protected View). See docs/04-identity-security.md §5.
/// </summary>
public interface IFileTagger
{
    void MarkAsDownloaded(string path);
}

public sealed class NullFileTagger : IFileTagger
{
    public static NullFileTagger Instance { get; } = new();

    public void MarkAsDownloaded(string path)
    {
    }
}
