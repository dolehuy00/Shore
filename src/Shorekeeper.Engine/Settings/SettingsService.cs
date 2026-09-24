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

    public event EventHandler<EffectiveSettings>? Changed;

    public ShorekeeperSettings User => user ?? throw NotLoaded();

    public EffectiveSettings Current => current ?? throw NotLoaded();

    public static string DefaultDisplayName => $"{Environment.UserName} · {Environment.MachineName}";

    public void Load()
    {
        lock (gate)
        {
            user = store.Load();
            current = Resolve(user);
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

    private EffectiveSettings Resolve(ShorekeeperSettings settings) =>
        SettingsResolver.Resolve(settings, policy, DefaultDisplayName, paths.DefaultDownloadDirectory);

    private static InvalidOperationException NotLoaded() => new("Settings have not been loaded yet.");
}
