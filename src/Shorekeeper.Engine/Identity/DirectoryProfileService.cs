using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Engine.Identity;

/// <summary>
/// Active Directory level 1 (docs/13-active-directory.md §2): the user's real name, department and picture.
/// The last answer is cached, so a laptop away from the office keeps showing them. Without a domain nothing changes.
/// </summary>
public sealed class DirectoryProfileService(
    IDirectoryInfo directory,
    AppPaths paths,
    SettingsService settings,
    TimeProvider timeProvider,
    ILogger<DirectoryProfileService> logger) : BackgroundService
{
    public const int MaxPhotoBytes = 100 * 1024;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(8);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);

    private volatile Snapshot? current;

    /// <summary>Raised when the profile is first known or changes.</summary>
    public event EventHandler? Changed;

    public DirectoryProfile? Profile => current?.Profile;

    /// <summary>Department as announced in presence (shortened to fit).</summary>
    public string? Department => current?.Department;

    /// <summary>Changes with the picture; null when there is none.</summary>
    public string? AvatarTag => current?.AvatarTag;

    public byte[]? Avatar => current?.Profile.Photo;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (LoadCache() is { } cached)
        {
            Apply(cached);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await LookupAsync(stoppingToken) is { } fresh)
            {
                Apply(fresh);
                SaveCache(fresh);
            }

            await Task.Delay(RefreshInterval, timeProvider, stoppingToken);
        }
    }

    private async Task<DirectoryProfile?> LookupAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The directory call cannot be cancelled; a slow domain controller is simply left behind.
            return await Task.Run(directory.GetCurrentUser, CancellationToken.None).WaitAsync(LookupTimeout, timeProvider, stoppingToken);
        }
        catch (TimeoutException)
        {
            logger.LogInformation("The company directory did not answer within {Timeout}", LookupTimeout);
            return null;
        }
    }

    private void Apply(DirectoryProfile profile)
    {
        if (profile.Photo is { Length: > MaxPhotoBytes })
        {
            profile = profile with { Photo = null };
        }

        var snapshot = new Snapshot(
            profile,
            string.IsNullOrWhiteSpace(profile.Department) ? null : Shorten(profile.Department.Trim(), PresenceCodec.MaxDepartmentLength),
            profile.Photo is { Length: > 0 } photo ? Convert.ToHexStringLower(SHA256.HashData(photo))[..8] : null);
        Snapshot? previous = current;
        current = snapshot;
        settings.SetDirectoryName(Shorten(profile.DisplayName, PresenceCodec.MaxNameLength));

        if (previous is null || previous.Department != snapshot.Department || previous.AvatarTag != snapshot.AvatarTag
            || previous.Profile.DisplayName != profile.DisplayName)
        {
            logger.LogInformation("Directory profile: {Name}, {Department}", profile.DisplayName, snapshot.Department);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private DirectoryProfile? LoadCache()
    {
        try
        {
            if (!File.Exists(paths.DirectoryCacheFile))
            {
                return null;
            }

            CachedProfile? cached = JsonSerializer.Deserialize(File.ReadAllBytes(paths.DirectoryCacheFile), DirectoryJsonContext.Default.CachedProfile);
            if (cached is null || string.IsNullOrWhiteSpace(cached.DisplayName))
            {
                return null;
            }

            byte[]? photo = File.Exists(paths.AvatarFile) ? File.ReadAllBytes(paths.AvatarFile) : null;
            return new DirectoryProfile(cached.DisplayName, cached.Department, cached.Title, cached.Email, photo);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Directory cache could not be read");
            return null;
        }
    }

    private void SaveCache(DirectoryProfile profile)
    {
        try
        {
            File.WriteAllBytes(paths.DirectoryCacheFile, JsonSerializer.SerializeToUtf8Bytes(
                new CachedProfile(profile.DisplayName, profile.Department, profile.Title, profile.Email), DirectoryJsonContext.Default.CachedProfile));
            if (profile.Photo is { Length: > 0 and <= MaxPhotoBytes } photo)
            {
                File.WriteAllBytes(paths.AvatarFile, photo);
            }
            else
            {
                File.Delete(paths.AvatarFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Directory cache could not be saved");
        }
    }

    private static string Shorten(string text, int max) => text.Length > max ? text[..max] : text;

    private sealed record Snapshot(DirectoryProfile Profile, string? Department, string? AvatarTag);
}

internal sealed record CachedProfile(string DisplayName, string? Department, string? Title, string? Email);

[JsonSerializable(typeof(CachedProfile))]
internal sealed partial class DirectoryJsonContext : JsonSerializerContext;
