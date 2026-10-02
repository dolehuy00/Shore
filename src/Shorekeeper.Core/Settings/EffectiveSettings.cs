namespace Shorekeeper.Core.Settings;

/// <summary>
/// Settings actually in force: defaults, then user choices, then IT policies.
/// </summary>
public sealed record EffectiveSettings
{
    public const int DefaultDiscoveryPort = 47470;
    public const int DefaultApiPort = 47471;
    public static readonly TimeSpan DefaultOfferLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultReconnectWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinReconnectWindow = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxReconnectWindow = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan MaxOfferLifetime = TimeSpan.FromDays(30);

    public required string DisplayName { get; init; }

    public required string DownloadDirectory { get; init; }

    public bool CreateSenderSubfolders { get; init; }

    public TimeSpan OfferLifetime { get; init; } = DefaultOfferLifetime;

    public TimeSpan ReconnectWindow { get; init; } = DefaultReconnectWindow;

    public bool AlwaysSnapshotBeforeSend { get; init; }

    public int DiscoveryPort { get; init; } = DefaultDiscoveryPort;

    public int ApiPort { get; init; } = DefaultApiPort;

    public bool BridgeEnabled { get; init; }

    public IReadOnlyList<string> BridgeAddresses { get; init; } = [];

    public IReadOnlyList<string> ProbeSubnets { get; init; } = [];

    /// <summary>False when IT turned groups off (policy <c>DisableGroups</c>).</summary>
    public bool GroupsEnabled { get; init; } = true;

    /// <summary>
    /// Names of <see cref="ShorekeeperSettings"/> properties that are locked by policy.
    /// The UI shows these as read-only ("Do quản trị viên đặt").
    /// </summary>
    public IReadOnlySet<string> LockedSettings { get; init; } = new HashSet<string>();

    public bool IsLocked(string settingName) => LockedSettings.Contains(settingName);
}
