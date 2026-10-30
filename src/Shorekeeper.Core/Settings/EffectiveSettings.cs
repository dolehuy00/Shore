using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Platform;

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
    public const double DefaultAutoAcceptLimitGb = 2;
    public const double MaxAutoAcceptLimitGb = 10_000;

    public required string DisplayName { get; init; }

    public required string DownloadDirectory { get; init; }

    public bool CreateSenderSubfolders { get; init; }

    public TimeSpan OfferLifetime { get; init; } = DefaultOfferLifetime;

    public TimeSpan ReconnectWindow { get; init; } = DefaultReconnectWindow;

    public bool AlwaysSnapshotBeforeSend { get; init; }

    /// <summary>"Tự nhận file" limit for contacts that follow Settings.</summary>
    public double AutoAcceptLimitGb { get; init; } = DefaultAutoAcceptLimitGb;

    public long AutoAcceptMaxBytes => (long)(AutoAcceptLimitGb * (1L << 30));

    /// <summary>
    /// Pictures a contact sends (not through a group, all pictures, up to 20 MB in all) download by themselves, so
    /// they show in the conversation (docs/15-chat.md §6.3).
    /// </summary>
    public bool ChatAutoDownloadPictures { get; init; } = true;

    public bool QuickSendHotkey { get; init; } = true;

    public Hotkey QuickSendShortcut { get; init; } = Hotkey.Default;

    public AudioLatency AudioLatency { get; init; }

    /// <inheritdoc cref="ShorekeeperSettings.AudioOutputDevice"/>
    public string? AudioOutputDevice { get; init; }

    /// <inheritdoc cref="ShorekeeperSettings.AudioControllers"/>
    public IReadOnlySet<string> AudioControllers { get; init; } = new HashSet<string>();

    /// <summary>"Âm lượng" goes up to this: a quiet source can be heard louder than it plays.</summary>
    public const int MaxListenVolume = 150;

    /// <inheritdoc cref="ShorekeeperSettings.ListenVolumes"/>
    public IReadOnlyDictionary<string, int> ListenVolumes { get; init; } = new Dictionary<string, int>();

    /// <summary>"Phát âm thanh" on a phone plays its microphone rather than what apps play.</summary>
    public bool AudioFromMicrophone { get; init; }

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
