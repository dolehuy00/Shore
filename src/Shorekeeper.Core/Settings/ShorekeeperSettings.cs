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
}
