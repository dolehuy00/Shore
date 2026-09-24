using Shorekeeper.Core.Platform;

namespace Shorekeeper.Core.Settings;

/// <summary>
/// Combines defaults, user settings and IT policies into <see cref="EffectiveSettings"/>.
/// Pure function so the precedence rules are easy to test.
/// </summary>
public static class SettingsResolver
{
    public static EffectiveSettings Resolve(
        ShorekeeperSettings user,
        IPolicyProvider policy,
        string defaultDisplayName,
        string defaultDownloadDirectory)
    {
        var locked = new HashSet<string>(StringComparer.Ordinal);

        string downloadDirectory = NonEmpty(user.DownloadDirectory) ?? defaultDownloadDirectory;
        if (NonEmpty(policy.GetString(PolicyNames.DownloadDirectory)) is { } policyDownloadDirectory)
        {
            downloadDirectory = Environment.ExpandEnvironmentVariables(policyDownloadDirectory);
            locked.Add(nameof(ShorekeeperSettings.DownloadDirectory));
        }

        TimeSpan offerLifetime = user.OfferLifetimeHours is > 0
            ? TimeSpan.FromHours(user.OfferLifetimeHours.Value)
            : EffectiveSettings.DefaultOfferLifetime;
        offerLifetime = Min(offerLifetime, EffectiveSettings.MaxOfferLifetime);
        if (policy.GetInt32(PolicyNames.MaxOfferLifetimeHours) is > 0 and var maxHours)
        {
            // A ceiling, not a fixed value: the user may still choose something shorter.
            offerLifetime = Min(offerLifetime, TimeSpan.FromHours(maxHours));
        }

        TimeSpan reconnectWindow = user.ReconnectWindowSeconds is > 0
            ? TimeSpan.FromSeconds(user.ReconnectWindowSeconds.Value)
            : EffectiveSettings.DefaultReconnectWindow;
        if (policy.GetInt32(PolicyNames.ReconnectWindowSeconds) is > 0 and var policyReconnect)
        {
            reconnectWindow = TimeSpan.FromSeconds(policyReconnect);
            locked.Add(nameof(ShorekeeperSettings.ReconnectWindowSeconds));
        }

        reconnectWindow = Clamp(reconnectWindow, EffectiveSettings.MinReconnectWindow, EffectiveSettings.MaxReconnectWindow);

        int discoveryPort = ValidPort(user.DiscoveryPort) ?? EffectiveSettings.DefaultDiscoveryPort;
        if (ValidPort(policy.GetInt32(PolicyNames.DiscoveryPort)) is { } policyDiscoveryPort)
        {
            discoveryPort = policyDiscoveryPort;
            locked.Add(nameof(ShorekeeperSettings.DiscoveryPort));
        }

        int apiPort = ValidPort(user.ApiPort) ?? EffectiveSettings.DefaultApiPort;
        if (ValidPort(policy.GetInt32(PolicyNames.ApiPort)) is { } policyApiPort)
        {
            apiPort = policyApiPort;
            locked.Add(nameof(ShorekeeperSettings.ApiPort));
        }

        return new EffectiveSettings
        {
            DisplayName = NonEmpty(user.DisplayName)?.Trim() ?? defaultDisplayName,
            DownloadDirectory = downloadDirectory,
            CreateSenderSubfolders = user.CreateSenderSubfolders,
            OfferLifetime = offerLifetime,
            ReconnectWindow = reconnectWindow,
            AlwaysSnapshotBeforeSend = user.AlwaysSnapshotBeforeSend,
            DiscoveryPort = discoveryPort,
            ApiPort = apiPort,
            LockedSettings = locked,
        };
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ValidPort(int? port) => port is > 0 and <= 65535 ? port : null;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
