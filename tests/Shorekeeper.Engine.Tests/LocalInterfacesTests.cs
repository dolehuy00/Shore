using System.Net.NetworkInformation;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Engine.Tests;

public class LocalInterfacesTests
{
    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection I219-V", NetworkInterfaceType.Ethernet, true, null)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211, true, null)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, true, "Adapter ảo")]
    [InlineData("Ethernet 2", "VirtualBox Host-Only Ethernet Adapter", NetworkInterfaceType.Ethernet, true, "Adapter ảo")]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, true, "Adapter ảo")]
    [InlineData("Teredo", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel, false, "Tunnel")]
    [InlineData("Ethernet 3", "Some NIC", NetworkInterfaceType.Ethernet, false, "Không hỗ trợ multicast")]
    public void Virtual_and_unsuitable_adapters_are_excluded(
        string name, string description, NetworkInterfaceType type, bool multicast, string? expectedReason)
    {
        Assert.Equal(expectedReason, LocalInterfaces.GetExclusionReason(name, description, type, multicast));
    }
}
