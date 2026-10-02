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

    public int? DiscoveryPort { get; init; }

    public int? ApiPort { get; init; }

    /// <summary>"Làm cầu nối cho mạng": relay presence so other subnets see each other.</summary>
    public bool EnableBridge { get; init; }

    /// <summary>Bridges to register with: "host" or "host:port" (HTTPS API port).</summary>
    public IReadOnlyList<string>? BridgeAddresses { get; init; }

    /// <summary>IPv4 ranges in CIDR notation (at most /22 each) probed for peers.</summary>
    public IReadOnlyList<string>? ProbeSubnets { get; init; }
}
