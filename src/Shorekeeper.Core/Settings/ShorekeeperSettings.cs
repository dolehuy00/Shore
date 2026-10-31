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

    /// <summary>Pictures from contacts download by themselves to show in the conversation (on when null).</summary>
    public bool? ChatAutoDownloadPictures { get; init; }

    /// <summary>"vi" or "en"; null follows the system language (docs/10-ux.md §1). Takes effect at the next start.</summary>
    public string? Language { get; init; }

    /// <summary>"light" or "dark"; null follows the system's light or dark mode. Takes effect at once.</summary>
    public string? Theme { get; init; }

    /// <summary>The shortcut opens "Gửi nhanh" from anywhere (on by default).</summary>
    public bool? QuickSendHotkey { get; init; }

    /// <summary>That shortcut, e.g. "Ctrl+Alt+S" (the default when null or not a valid shortcut).</summary>
    public string? QuickSendShortcut { get; init; }

    /// <summary>"low", "stable" or "preserve": how much sound the listener keeps in hand (low when null).</summary>
    public string? AudioLatency { get; init; }

    /// <summary>"Phát ra" (computer): the device "Nghe" plays on, by its system id; null for the default one.</summary>
    public string? AudioOutputDevice { get; init; }

    /// <summary>"Cho điều khiển": listeners (device ids) that may press media keys here; nobody when null.</summary>
    public List<string>? AudioControllers { get; init; }

    /// <summary>"Âm lượng" while listening, in percent by source device id; a source not here plays at 100.</summary>
    public Dictionary<string, int>? ListenVolumes { get; init; }

    /// <summary>"Nguồn tiếng" (phone): "mic" plays the microphone, anything else what apps play.</summary>
    public string? AudioSource { get; init; }

    /// <summary>"Tự gửi ảnh" (phone): the computer new photos go to, by device id; null when it is off.</summary>
    public string? PhotoBackupTarget { get; init; }

    /// <summary>What it sends, as in <see cref="Platform.MediaKindNames"/>; null for camera photos and videos.</summary>
    public string? PhotoBackupKinds { get; init; }

    /// <summary>The library marker up to which everything has been sent and taken (or was there when it was turned on).</summary>
    public long? PhotoBackupSent { get; init; }

    /// <summary>The offer on its way now, and the marker of its newest item.</summary>
    public string? PhotoBackupOffer { get; init; }

    public long? PhotoBackupOfferUpTo { get; init; }

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
