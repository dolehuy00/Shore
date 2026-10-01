namespace Shorekeeper.Core.Platform;

[Flags]
public enum NetworkProfiles
{
    None = 0,
    Domain = 1,
    Private = 2,
    Public = 4,
}

/// <param name="ActiveProfiles">Network profiles (Domain/Private/Public) currently active on this machine.</param>
/// <param name="FirewallEnabled">Whether the firewall is on for any active profile.</param>
/// <param name="HasAllowRule">An enabled inbound allow rule exists for this program on an active profile.</param>
/// <param name="HasBlockRule">An enabled inbound block rule exists for this program on an active profile (e.g. the user clicked "Cancel" on the Windows prompt).</param>
public sealed record FirewallStatus(NetworkProfiles ActiveProfiles, bool FirewallEnabled, bool HasAllowRule, bool HasBlockRule);

/// <summary>Reads (never changes) the OS firewall state for the network diagnostics screen.</summary>
public interface IFirewallInspector
{
    /// <returns>Null when the state cannot be determined on this platform.</returns>
    FirewallStatus? Inspect(string programPath);
}

public sealed class NullFirewallInspector : IFirewallInspector
{
    public static NullFirewallInspector Instance { get; } = new();

    public FirewallStatus? Inspect(string programPath) => null;
}
