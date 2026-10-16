using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;

namespace Shorekeeper.Engine.Settings;

/// <summary>
/// Holds the user's settings and the effective settings after applying IT policies.
/// </summary>
public sealed class SettingsService(AppPaths paths, SettingsStore store, IPolicyProvider policy)
{
    private readonly Lock gate = new();
    private ShorekeeperSettings? user;
    private EffectiveSettings? current;
    private string? directoryName;

    public event EventHandler<EffectiveSettings>? Changed;

    public ShorekeeperSettings User => user ?? throw NotLoaded();

    public EffectiveSettings Current => current ?? throw NotLoaded();

    /// <summary>"user · PC" until the user picks a name; Android sets the phone's name (there is no user name).</summary>
    public static string DefaultDisplayName { get; set; } = $"{Environment.UserName} · {Environment.MachineName}";

    public void Load()
    {
        lock (gate)
        {
            user = store.Load();
            current = Resolve(user);
            L.Use(user.Language);
        }
    }

    public EffectiveSettings Update(Func<ShorekeeperSettings, ShorekeeperSettings> change)
    {
        EffectiveSettings updated;
        lock (gate)
        {
            ShorekeeperSettings next = change(User);
            store.Save(next);
            user = next;
            current = updated = Resolve(next);
        }

        Changed?.Invoke(this, updated);
        return updated;
    }

    /// <summary>
    /// The user's real name from the company directory (docs/13-active-directory.md §2) replaces the default
    /// "user · PC" name; a name the user typed still wins.
    /// </summary>
    public void SetDirectoryName(string? name)
    {
        EffectiveSettings? updated = null;
        lock (gate)
        {
            directoryName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            EffectiveSettings resolved = Resolve(User);
            if (resolved.DisplayName != Current.DisplayName)
            {
                current = updated = resolved;
            }
        }

        if (updated is not null)
        {
            Changed?.Invoke(this, updated);
        }
    }

    private EffectiveSettings Resolve(ShorekeeperSettings settings) =>
        SettingsResolver.Resolve(settings, policy, directoryName ?? DefaultDisplayName, paths.DefaultDownloadDirectory);

    private static InvalidOperationException NotLoaded() => new("Settings have not been loaded yet.");
}
