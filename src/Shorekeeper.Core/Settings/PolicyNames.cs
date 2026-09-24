namespace Shorekeeper.Core.Settings;

/// <summary>
/// Policy value names under <c>Software\Policies\Shorekeeper</c>.
/// See docs/11-windows-deployment.md §4.
/// </summary>
public static class PolicyNames
{
    public const string DownloadDirectory = "DownloadDirectory";
    public const string MaxOfferLifetimeHours = "MaxOfferLifetimeHours";
    public const string ReconnectWindowSeconds = "ReconnectWindowSeconds";
    public const string DiscoveryPort = "DiscoveryPort";
    public const string ApiPort = "ApiPort";
    public const string MaxOfferSizeGB = "MaxOfferSizeGB";
    public const string ApplyMarkOfTheWeb = "ApplyMarkOfTheWeb";
    public const string AllowedSubnets = "AllowedSubnets";
    public const string DisableGroups = "DisableGroups";
    public const string AllowBridge = "AllowBridge";
    public const string EnableBridge = "EnableBridge";
    public const string BridgeAddresses = "BridgeAddresses";
    public const string ProbeSubnets = "ProbeSubnets";
    public const string AuditLog = "AuditLog";
}
