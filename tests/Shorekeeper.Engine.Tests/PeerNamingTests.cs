using System.Security.Cryptography;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

/// <summary>Two people with the same name can be told apart on screen (docs/10-ux.md §2).</summary>
public sealed class PeerNamingTests : IAsyncLifetime
{
    private TestNode node = null!;

    public async ValueTask InitializeAsync() => node = await TestNode.StartAsync("Huy");

    public async ValueTask DisposeAsync() => await node.DisposeAsync();

    private PeerNaming Naming => node.Get<PeerNaming>();

    [Fact]
    public async Task A_name_nobody_else_has_stays_as_it_is()
    {
        DeviceId mai = NewId();
        await node.Trust.SetTrustedAsync(mai, "Mai", "PC-QA-07", Ct);

        Assert.Equal("Mai", Naming.Of(mai));
    }

    [Fact]
    public async Task The_same_name_gets_the_computer_name_or_the_short_id_when_that_is_the_same_too()
    {
        DeviceId desk = NewId();
        DeviceId laptop = NewId();
        DeviceId secondSession = NewId();
        await node.Trust.SetTrustedAsync(desk, "Tuấn", "PC-DEV-03", Ct);
        await node.Trust.SetTrustedAsync(laptop, "Tuấn", "LAPTOP-TUAN", Ct);
        await node.Trust.SetTrustedAsync(secondSession, "tuấn", "PC-DEV-03", Ct);

        Assert.Equal("Tuấn · LAPTOP-TUAN", Naming.Of(laptop));
        Assert.Equal($"Tuấn · {desk.ShortForm}", Naming.Of(desk));
        Assert.Equal($"tuấn · {secondSession.ShortForm}", Naming.Of(secondSession));
    }

    [Fact]
    public async Task An_alias_or_a_change_of_name_tells_them_apart_at_once()
    {
        DeviceId one = NewId();
        DeviceId two = NewId();
        await node.Trust.SetTrustedAsync(one, "Lan", "PC-HR-01", Ct);
        await node.Trust.SetTrustedAsync(two, "Lan", "PC-HR-02", Ct);
        Assert.Equal("Lan · PC-HR-01", Naming.Of(one));

        await node.Trust.SetAliasAsync(two, "Lan kế toán", Ct);

        Assert.Equal("Lan", Naming.Of(one));
        Assert.Equal("Lan kế toán", Naming.Of(two));
    }

    [Fact]
    public async Task Someone_known_only_by_the_name_they_came_with_is_told_apart_too()
    {
        DeviceId contact = NewId();
        DeviceId stranger = NewId();
        await node.Trust.SetTrustedAsync(contact, "Minh", "PC-ACC-02", Ct);

        // Not someone we know (e.g. a member who left): only they carry the difference.
        Assert.Equal($"Minh · {stranger.ShortForm}", Naming.Of(stranger, "Minh"));
        Assert.Equal("Minh", Naming.Of(contact));
        Assert.Equal(stranger.ShortForm, Naming.Of(stranger));
    }

    [Fact]
    public async Task Our_own_name_does_not_count()
    {
        DeviceId other = NewId();
        await node.Trust.SetTrustedAsync(other, "Huy", "LAPTOP", Ct);
        await node.Groups.CreateAsync("Build QA", Ct);

        Assert.Equal("Huy", Naming.Of(other));
    }

    private static DeviceId NewId() => DeviceId.FromSubjectPublicKeyInfo(RandomNumberGenerator.GetBytes(32));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
