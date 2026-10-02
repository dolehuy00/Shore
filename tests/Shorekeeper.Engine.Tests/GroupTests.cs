using System.Security.Cryptography;
using Shorekeeper.Core.Discovery;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Transfers;

namespace Shorekeeper.Engine.Tests;

/// <summary>Groups between real engines over HTTPS (docs/07-groups.md). Nobody here is a contact of anybody.</summary>
public sealed class GroupTests : IAsyncLifetime
{
    private TestNode host = null!;
    private TestNode mai = null!;
    private TestNode tuan = null!;

    public async ValueTask InitializeAsync()
    {
        host = await TestNode.StartAsync("Huy");
        mai = await TestNode.StartAsync("Mai");
        tuan = await TestNode.StartAsync("Tuấn");
        // What discovery would do: everyone knows where everyone listens.
        foreach (TestNode node in (TestNode[])[host, mai, tuan])
        {
            foreach (TestNode other in (TestNode[])[host, mai, tuan])
            {
                if (node != other)
                {
                    node.Sees(other);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await mai.DisposeAsync();
        await tuan.DisposeAsync();
    }

    [Fact]
    public async Task New_group_is_announced_in_presence()
    {
        Group group = await host.Groups.CreateAsync("  Build QA ", Ct);

        Assert.Equal("Build QA", group.Name);
        Assert.Equal(GroupRole.Host, group.Role);
        PresencePacket packet = host.Get<ILocalPresence>().CreatePacket(PresencePacketTypes.Heartbeat);
        Assert.Equal([new PresenceGroup(group.Id, "Build QA", 1)], packet.Groups);
    }

    [Fact]
    public async Task Join_request_approved_by_the_host_makes_a_member()
    {
        Group group = await host.Groups.CreateAsync("Build QA", Ct);
        JoinRequest? asked = null;
        host.Groups.JoinRequested += (_, request) => asked = request;

        await mai.Groups.RequestJoinAsync(host.Id, group.Id, group.Name, "Cho mình vào với", Ct);

        Assert.Equal(GroupRole.Pending, mai.GroupStore.Get(group.Id)!.Role);
        Assert.Equal("Mai", asked!.Name);
        Assert.Equal("Cho mình vào với", asked.Note);

        await host.Groups.ApproveAsync(group.Id, mai.Id);

        await WaitUntil(() => mai.GroupStore.Get(group.Id) is { Role: GroupRole.Member });
        Group seen = mai.GroupStore.Get(group.Id)!;
        Assert.Equal(["Huy", "Mai"], seen.Members.Select(m => m.Name).Order());
        Assert.True(seen.Members.Single(m => m.DeviceId == host.Id).IsHost);
        Assert.True(mai.GroupStore.IsMember(group.Id, host.Id));
        Assert.Empty(host.GroupStore.GetRequests(group.Id));
    }

    [Fact]
    public async Task Declined_request_is_forgotten_and_three_declines_lock_the_group()
    {
        Group group = await host.Groups.CreateAsync("Build QA", Ct);
        var notices = new List<GroupNotice>();
        mai.Groups.Notice += (_, notice) => notices.Add(notice);

        for (int i = 0; i < 3; i++)
        {
            await mai.Groups.RequestJoinAsync(host.Id, group.Id, group.Name, null, Ct);
            await WaitUntil(() => host.GroupStore.GetRequest(group.Id, mai.Id) is { State: JoinRequestState.Pending });
            await host.Groups.DeclineAsync(group.Id, mai.Id);
            await WaitUntil(() => mai.GroupStore.Get(group.Id) is null);
        }

        Assert.Equal(3, notices.Count);
        PeerApiException locked = await Assert.ThrowsAsync<PeerApiException>(() => mai.Groups.RequestJoinAsync(host.Id, group.Id, group.Name, null, Ct));
        Assert.Equal(ApiErrorCodes.RateLimited, locked.Code);
    }

    [Fact]
    public async Task Invited_peer_joins_without_waiting_for_approval()
    {
        Group group = await host.Groups.CreateAsync("Build QA", Ct);
        GroupInvitation? invitation = null;
        tuan.Groups.InvitationReceived += (_, received) => invitation = received;

        await host.Groups.InviteAsync(group.Id, tuan.Id, "Tuấn", "PC-DEV-11", Ct);

        Assert.Equal(new GroupInvitation(group.Id, "Build QA", host.Id, "Huy"), invitation);
        Assert.Equal(JoinRequestState.Invited, host.GroupStore.GetRequest(group.Id, tuan.Id)!.State);

        await tuan.Groups.AcceptInvitationAsync(invitation!, Ct);

        Assert.True(host.GroupStore.IsMember(group.Id, tuan.Id));
        await WaitUntil(() => tuan.GroupStore.Get(group.Id) is { Role: GroupRole.Member });
        Assert.Empty(tuan.Groups.GetInvitations());
    }

    [Fact]
    public async Task Members_send_files_to_each_other_without_connecting()
    {
        Group group = await GroupWithMaiAndTuanAsync();
        byte[] content = RandomNumberGenerator.GetBytes(256 * 1024);
        string source = Path.Combine(mai.WorkDirectory, "report.xlsx");
        await File.WriteAllBytesAsync(source, content, Ct);

        string offerId = await mai.Offers.CreateAsync(OfferService.Prepare([source]), [tuan.Id], null, snapshotLocked: false, group.Id, Ct);

        ReceivedOffer offer = await WaitForOfferAsync(tuan, offerId);
        var key = new OfferKey(mai.Id, offerId);
        tuan.Inbox.Download(key);
        await WaitUntil(() => tuan.Inbox.Get(key) is { State: InboxState.Completed });
        Assert.Equal(content, await File.ReadAllBytesAsync(tuan.Inbox.Get(key)!.Files.Single().FinalPath!, Ct));
        Assert.Equal(mai.Id, offer.SenderId);
    }

    [Fact]
    public async Task Offer_outside_the_group_is_refused()
    {
        await GroupWithMaiAndTuanAsync();
        string source = Path.Combine(mai.WorkDirectory, "a.txt");
        await File.WriteAllTextAsync(source, "x", Ct);

        // No group id: Tuấn is not Mai's contact, so he refuses it.
        await mai.Offers.CreateAsync(OfferService.Prepare([source]), [tuan.Id], null, snapshotLocked: false, groupId: null, Ct);

        await WaitUntil(() => mai.Offers.GetOffers().Single().Recipients.Single().State == RecipientState.Failed);
        Assert.Empty(tuan.Inbox.GetOffers());
    }

    [Fact]
    public async Task Removed_member_loses_the_group_and_its_permissions()
    {
        Group group = await GroupWithMaiAndTuanAsync();
        var notices = new List<GroupNotice>();
        tuan.Groups.Notice += (_, notice) => notices.Add(notice);

        await host.Groups.RemoveMemberAsync(group.Id, tuan.Id);

        await WaitUntil(() => tuan.GroupStore.Get(group.Id) is null);
        Assert.Contains("không còn", Assert.Single(notices).Message, StringComparison.Ordinal);
        await WaitUntil(() => !mai.GroupStore.IsMember(group.Id, tuan.Id));
        Assert.False(mai.GroupStore.CanExchangeFiles(tuan.Id, group.Id));
    }

    [Fact]
    public async Task Members_leave_and_closing_the_group_reaches_everyone()
    {
        Group group = await GroupWithMaiAndTuanAsync();

        await tuan.Groups.LeaveAsync(group.Id);

        Assert.Null(tuan.GroupStore.Get(group.Id));
        Assert.False(host.GroupStore.IsMember(group.Id, tuan.Id));
        await WaitUntil(() => mai.GroupStore.Get(group.Id)!.Members.Count == 2);

        await host.Groups.CloseAsync(group.Id);

        await WaitUntil(() => mai.GroupStore.Get(group.Id) is null);
        Assert.Null(host.GroupStore.Get(group.Id));
    }

    [Fact]
    public async Task Only_members_and_requesters_learn_anything_about_a_group()
    {
        Group group = await host.Groups.CreateAsync("Build QA", Ct);

        PeerApiException error = await Assert.ThrowsAsync<PeerApiException>(async () =>
        {
            using HttpResponseMessage response = await tuan.Client.SendAsync(
                host.Id, b => new HttpRequestMessage(HttpMethod.Get, new Uri(b, $"groups/{group.Id}/state")), TimeSpan.FromSeconds(10), Ct);
            await PeerClient.ReadAsync<GroupStateResponse>(response, Ct);
        });

        Assert.Equal(404, error.Status);
    }

    /// <summary>Huy hosts "Build QA"; Mai and Tuấn joined and see each other in the member list.</summary>
    private async Task<Group> GroupWithMaiAndTuanAsync()
    {
        Group group = await host.Groups.CreateAsync("Build QA", Ct);
        foreach (TestNode member in (TestNode[])[mai, tuan])
        {
            await host.Groups.InviteAsync(group.Id, member.Id, member.Id.ShortForm, "PC", Ct);
            await member.Groups.AcceptInvitationAsync(member.Groups.GetInvitations().Single(), Ct);
        }

        await WaitUntil(() => mai.GroupStore.Get(group.Id) is { Members.Count: 3 } && tuan.GroupStore.Get(group.Id) is { Members.Count: 3 });
        return group;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ReceivedOffer> WaitForOfferAsync(TestNode recipient, string offerId)
    {
        ReceivedOffer? offer = null;
        await WaitUntil(() => (offer = recipient.Inbox.GetOffers().FirstOrDefault(o => o.OfferId == offerId)) is not null);
        return offer!;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 15s.");
            await Task.Delay(50, Ct);
        }
    }
}
