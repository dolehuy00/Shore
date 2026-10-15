namespace Shorekeeper.Core.Platform;

/// <summary>The signed-in user as the company directory (Active Directory) knows them. See docs/13-active-directory.md §2.</summary>
/// <param name="Photo">Small picture (AD <c>thumbnailPhoto</c>, usually JPEG, at most 100 KB).</param>
public sealed record DirectoryProfile(string DisplayName, string? Department, string? Title, string? Email, byte[]? Photo);

/// <summary>Reads the current user's profile from the company directory.</summary>
public interface IDirectoryInfo
{
    /// <summary>Blocking; may take seconds when the directory is slow.</summary>
    /// <returns>Null when this computer is not in a domain or the directory cannot be reached.</returns>
    DirectoryProfile? GetCurrentUser();
}

public sealed class NullDirectoryInfo : IDirectoryInfo
{
    public static NullDirectoryInfo Instance { get; } = new();

    public DirectoryProfile? GetCurrentUser() => null;
}
