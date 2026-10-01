using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows.Tests;

public class WindowsFirewallInspectorTests
{
    [Fact]
    public void Reports_active_profile_for_this_machine()
    {
        FirewallStatus? status = new WindowsFirewallInspector().Inspect(Environment.ProcessPath!);

        Assert.NotNull(status);
        Assert.NotEqual(NetworkProfiles.None, status.ActiveProfiles);
    }

    [Fact]
    public void Unknown_program_has_no_rules()
    {
        FirewallStatus? status = new WindowsFirewallInspector().Inspect(@"C:\does-not-exist\nothing.exe");

        Assert.NotNull(status);
        Assert.False(status.HasAllowRule);
        Assert.False(status.HasBlockRule);
    }
}
