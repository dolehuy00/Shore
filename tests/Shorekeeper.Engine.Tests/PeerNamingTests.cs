using System.Net;
using System.Security.Cryptography;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Settings;
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
    public async Task A_contact_that_renames_itself_is_shown_by_its_new_name()
    {
        await using TestNode mai = await TestNode.StartAsync("Mai");
        await node.ConnectAsync(mai);
        Assert.Equal("peer", Naming.Of(mai.Id));

        mai.Get<SettingsService>().Update(s => s with { DisplayName = "Mai kế toán" });
        node.Directory.Observe(
            new PresencePacket { Type = PresencePacketTypes.Heartbeat, Id = mai.Id.Value, Name = "Mai kế toán", Port = mai.ApiPort },
            IPAddress.Loopback);

        await WaitUntil(() => Naming.Of(mai.Id) == "Mai kế toán");
        Assert.Equal("Mai kế toán", node.Trust.Get(mai.Id)!.DisplayName);
    }

    [Fact]
    public async Task A_name_announced_by_someone_else_for_a_contact_is_not_taken()
    {
        await using TestNode mai = await TestNode.StartAsync("Mai");
        await node.ConnectAsync(mai);
        await using TestNode impostor = await TestNode.StartAsync("Impostor");

        // A packet with Mai's id and another name, pointing at a device that is not Mai: its certificate gives it away.
        node.Directory.Observe(
            new PresencePacket { Type = PresencePacketTypes.Heartbeat, Id = mai.Id.Value, Name = "Giám đốc", Port = impostor.ApiPort },
            IPAddress.Loopback);
        await Task.Delay(1500, Ct);

        // Only what Mai's own device says is kept: never the name in the forged packet.
        Assert.NotEqual("Giám đốc", node.Trust.Get(mai.Id)!.DisplayName);
        Assert.NotEqual("Giám đốc", Naming.Of(mai.Id));
    }

    [Fact]
    public async Task An_alias_still_wins_over_the_name_a_contact_gives_itself()
    {
        await using TestNode mai = await TestNode.StartAsync("Mai");
        await node.ConnectAsync(mai);
        await node.Trust.SetAliasAsync(mai.Id, "Mai phòng nhân sự", Ct);

        mai.Get<SettingsService>().Update(s => s with { DisplayName = "Mai mới" });
        node.Directory.Observe(
            new PresencePacket { Type = PresencePacketTypes.Heartbeat, Id = mai.Id.Value, Name = "Mai mới", Port = mai.ApiPort },
            IPAddress.Loopback);

        await WaitUntil(() => node.Trust.Get(mai.Id)!.DisplayName == "Mai mới");
        Assert.Equal("Mai phòng nhân sự", Naming.Of(mai.Id));
    }

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

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            await Task.Delay(50, Ct);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
