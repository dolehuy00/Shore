namespace Shorekeeper.Core.Settings;

/// <summary>
/// Settings chosen by the user, persisted in <c>settings.json</c>.
/// Null means "use the default". Policies may override any of them,
/// see <see cref="EffectiveSettings"/>.
/// </summary>
public sealed record ShorekeeperSettings
{
    public string? DisplayName { get; init; }

    public string? DownloadDirectory { get; init; }

    public bool CreateSenderSubfolders { get; init; }

    public int? OfferLifetimeHours { get; init; }

    public int? ReconnectWindowSeconds { get; init; }

    public bool AlwaysSnapshotBeforeSend { get; init; }

    /// <summary>"Tự nhận file" limit in GB for contacts that follow Settings (2 when null).</summary>
    public double? AutoAcceptLimitGb { get; init; }

    /// <summary>"vi" or "en"; null follows the system language (docs/10-ux.md §1). Takes effect at the next start.</summary>
    public string? Language { get; init; }

    /// <summary>The shortcut opens "Gửi nhanh" from anywhere (on by default).</summary>
    public bool? QuickSendHotkey { get; init; }

    /// <summary>That shortcut, e.g. "Ctrl+Alt+S" (the default when null or not a valid shortcut).</summary>
    public string? QuickSendShortcut { get; init; }

    /// <summary>"low", "stable" or "preserve": how much sound the listener keeps in hand (low when null).</summary>
    public string? AudioLatency { get; init; }

    /// <summary>"Phát ra" (computer): the device "Nghe" plays on, by its system id; null for the default one.</summary>
    public string? AudioOutputDevice { get; init; }

    /// <summary>"Nguồn tiếng" (phone): "mic" plays the microphone, anything else what apps play.</summary>
    public string? AudioSource { get; init; }

    /// <summary>"Phát âm thanh" asks listeners for this password; a hash from <see cref="Audio.AudioPassword"/>, null for none.</summary>
    public string? AudioPasswordHash { get; init; }

    public int? DiscoveryPort { get; init; }

    public int? ApiPort { get; init; }

    /// <summary>"Làm cầu nối cho mạng": relay presence so other subnets see each other.</summary>
    public bool EnableBridge { get; init; }

    /// <summary>Bridges to register with: "host" or "host:port" (HTTPS API port).</summary>
    public IReadOnlyList<string>? BridgeAddresses { get; init; }

    /// <summary>IPv4 ranges in CIDR notation (at most /22 each) probed for peers.</summary>
    public IReadOnlyList<string>? ProbeSubnets { get; init; }
}
