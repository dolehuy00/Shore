using System.Net;
using Shorekeeper.Engine.Discovery;

namespace Shorekeeper.Engine.Tests;

public class SubnetScannerTests
{
    [Fact]
    public void Range_covers_its_hosts_without_network_and_broadcast()
    {
        // Host bits are ignored, as people often type their own address.
        var range = SubnetScanner.ParseRange("10.1.5.7/24", out string? error);

        Assert.Null(error);
        IPAddress[] hosts = [.. SubnetScanner.Expand(range!.Value.First, range.Value.PrefixLength)];
        Assert.Equal(254, hosts.Length);
        Assert.Equal(IPAddress.Parse("10.1.5.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("10.1.5.254"), hosts[^1]);
    }

    [Theory]
    [InlineData("10.1.5.9", 1)]
    [InlineData("10.1.5.8/31", 2)]
    [InlineData("10.1.4.0/22", 1022)]
    public void Small_ranges_and_single_addresses_work(string text, int count)
    {
        var range = SubnetScanner.ParseRange(text, out _)!.Value;

        Assert.Equal(count, SubnetScanner.Expand(range.First, range.PrefixLength).Count());
    }

    [Theory]
    [InlineData("10.0.0.0/16")]
    [InlineData("pc-dev-03")]
    [InlineData("10.1.5.0/24/1")]
    [InlineData("fe80::1/64")]
    public void Invalid_or_too_wide_ranges_are_refused_with_a_reason(string text)
    {
        Assert.Null(SubnetScanner.ParseRange(text, out string? error));
        Assert.NotNull(error);
    }
}
