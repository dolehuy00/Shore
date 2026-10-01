using System.Runtime.InteropServices;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// Reads Windows Defender Firewall state through the HNetCfg.FwPolicy2 COM object (read-only).
/// </summary>
public sealed class WindowsFirewallInspector : IFirewallInspector
{
    private const int DirectionInbound = 1;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;

    public FirewallStatus? Inspect(string programPath)
    {
        Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (policyType is null)
        {
            return null;
        }

        try
        {
            dynamic policy = Activator.CreateInstance(policyType)!;
            var active = (NetworkProfiles)(int)policy.CurrentProfileTypes;

            bool enabled = false;
            foreach (NetworkProfiles profile in (ReadOnlySpan<NetworkProfiles>)[NetworkProfiles.Domain, NetworkProfiles.Private, NetworkProfiles.Public])
            {
                enabled |= active.HasFlag(profile) && (bool)policy.FirewallEnabled[(int)profile];
            }

            string target = Normalize(programPath);
            bool allow = false;
            bool block = false;
            foreach (dynamic rule in policy.Rules)
            {
                if (!(bool)rule.Enabled
                    || (int)rule.Direction != DirectionInbound
                    || ((int)rule.Profiles & (int)active) == 0
                    || rule.ApplicationName is not string application
                    || !string.Equals(Normalize(application), target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int action = (int)rule.Action;
                allow |= action == ActionAllow;
                block |= action == ActionBlock;
            }

            return new FirewallStatus(active, enabled, allow, block);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
