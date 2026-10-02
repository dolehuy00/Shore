using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Shorekeeper.Engine.Discovery;

/// <param name="ExcludedReason">Null when the interface is used for discovery.</param>
public sealed record LocalInterface(string Name, string Description, IPAddress Address, int PrefixLength, string? ExcludedReason)
{
    public bool IsUsable => ExcludedReason is null;
}

/// <summary>
/// Picks the IPv4 interfaces used for multicast discovery (docs/03-discovery-presence.md §3.1).
/// Virtual adapters (Hyper-V, WSL, VirtualBox…) are skipped so we do not announce unreachable addresses.
/// </summary>
public static class LocalInterfaces
{
    private static readonly string[] VirtualAdapterMarkers =
        ["Hyper-V", "vEthernet", "VirtualBox", "VMware", "Docker", "WSL", "Npcap Loopback"];

    /// <summary>All IPv4 addresses of interfaces that are up, each marked usable or not (for diagnostics).</summary>
    public static IReadOnlyList<LocalInterface> GetAll()
    {
        var result = new List<LocalInterface>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            string? nicReason = GetExclusionReason(nic.Name, nic.Description, nic.NetworkInterfaceType, nic.SupportsMulticast);
            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                string? reason = nicReason ?? (IsLinkLocal(unicast.Address) ? "Chưa có IP (169.254.x.x)" : null);
                result.Add(new LocalInterface(nic.Name, nic.Description, unicast.Address, unicast.PrefixLength, reason));
            }
        }

        return result;
    }

    public static IReadOnlyList<LocalInterface> GetUsable() => [.. GetAll().Where(i => i.IsUsable)];

    /// <summary>True when <paramref name="address"/> is in the subnet of one of <paramref name="interfaces"/>, i.e. multicast reaches it.</summary>
    public static bool IsOnLink(IPAddress address, IReadOnlyList<LocalInterface> interfaces)
    {
        uint target = ToUInt32(address);
        foreach (LocalInterface nic in interfaces)
        {
            uint mask = nic.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - nic.PrefixLength);
            if ((ToUInt32(nic.Address) & mask) == (target & mask))
            {
                return true;
            }
        }

        return false;
    }

    private static uint ToUInt32(IPAddress address)
    {
        byte[] bytes = address.MapToIPv4().GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    internal static string? GetExclusionReason(string name, string description, NetworkInterfaceType type, bool supportsMulticast)
    {
        if (type == NetworkInterfaceType.Tunnel)
        {
            return "Tunnel";
        }

        if (!supportsMulticast)
        {
            return "Không hỗ trợ multicast";
        }

        foreach (string marker in VirtualAdapterMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || description.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return "Adapter ảo";
            }
        }

        return null;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
