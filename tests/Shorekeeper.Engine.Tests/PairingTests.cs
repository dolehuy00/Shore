using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

/// <summary>"Kết nối" end to end between two real engines (docs/04-identity-security.md §4).</summary>
public sealed class PairingTests : IAsyncLifetime
{
    private TestNode a = null!;
    private TestNode b = null!;

    public async ValueTask InitializeAsync()
    {
        a = await TestNode.StartAsync("A", services: s => s.AddSingleton<ManualPeerFinder>());
        b = await TestNode.StartAsync("B");
        a.Sees(b);
    }

    public async ValueTask DisposeAsync()
    {
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Accepted_request_makes_both_sides_trusted_with_the_same_code()
    {
        string? codeAtB = null;
        b.Pairing.IncomingRequested += (_, request) =>
        {
            codeAtB = request.Code;
            Assert.Equal(a.Id, request.PeerId);
            Assert.Equal("A", request.Name);
            b.Pairing.Accept(request.Id);
        };

        string? codeAtA = null;
        PairingOutcome outcome = await a.Pairing.RequestAsync(b.Id, "hi", pending => codeAtA = pending.Code, Ct);

        Assert.Equal(PairingOutcome.Accepted, outcome);
        Assert.NotNull(codeAtA);
        Assert.Equal(codeAtA, codeAtB);
        Assert.Equal(TrustLevel.Trusted, a.Trust.GetLevel(b.Id));
        await WaitUntil(() => b.Trust.GetLevel(a.Id) == TrustLevel.Trusted);
        Assert.Equal("B", a.Trust.Get(b.Id)!.DisplayName);
    }

    [Fact]
    public async Task An_invitation_from_the_QR_code_connects_without_asking()
    {
        bool asked = false;
        IncomingPairing? joined = null;
        b.Pairing.IncomingRequested += (_, _) => asked = true;
        b.Pairing.JoinedByInvite += (_, request) => joined = request;
        PairingInviteLink link = b.Pairing.CreateInvite();

        PairingOutcome outcome = await a.Pairing.RequestAsync(b.Id, null, _ => { }, Ct, link.Invite);

        Assert.Equal(PairingOutcome.Accepted, outcome);
        Assert.False(asked);
        Assert.Equal(TrustLevel.Trusted, a.Trust.GetLevel(b.Id));
        await WaitUntil(() => b.Trust.GetLevel(a.Id) == TrustLevel.Trusted);
        Assert.Equal(a.Id, joined?.PeerId);
    }

    [Fact]
    public async Task An_invitation_works_once_and_not_once_ended()
    {
        int asked = 0;
        b.Pairing.IncomingRequested += (_, request) =>
        {
            asked++;
            b.Pairing.Decline(request.Id);
        };
        PairingInviteLink ended = b.Pairing.CreateInvite();
        b.Pairing.EndInvite(ended.Invite);
        PairingInviteLink used = b.Pairing.CreateInvite();

        Assert.Equal(PairingOutcome.Declined, await a.Pairing.RequestAsync(b.Id, null, _ => { }, Ct, ended.Invite));
        Assert.Equal(PairingOutcome.Accepted, await a.Pairing.RequestAsync(b.Id, null, _ => { }, Ct, used.Invite));
        Assert.Equal(PairingOutcome.Declined, await a.Pairing.RequestAsync(b.Id, null, _ => { }, Ct, used.Invite));
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task The_QR_code_finds_only_the_device_it_names()
    {
        var finder = a.Get<ManualPeerFinder>();
        PairingInviteLink link = b.Pairing.CreateInvite() with { Endpoints = [new IPEndPoint(IPAddress.Loopback, b.ApiPort)] };
        DeviceId someoneElse = DeviceId.FromSubjectPublicKeyInfo([1, 2, 3]);

        FindResult found = await finder.FindAsync(link, Ct);
        FindResult wrong = await finder.FindAsync(link with { DeviceId = someoneElse }, Ct);
        FindResult own = await finder.FindAsync(link with { DeviceId = a.Id }, Ct);

        Assert.Equal(b.Id, found.Peer?.DeviceId);
        Assert.Null(wrong.Peer);
        Assert.NotNull(wrong.Error);
        Assert.Null(own.Peer);
    }

    [Fact]
    public async Task Responder_remembers_the_requester_address()
    {
        b.Pairing.IncomingRequested += (_, request) => b.Pairing.Accept(request.Id);

        await a.Pairing.RequestAsync(b.Id, null, _ => { }, Ct);
        await WaitUntil(() => b.Trust.GetLevel(a.Id) == TrustLevel.Trusted);

        var endpoints = await b.Trust.GetEndpointsAsync(a.Id, Ct);
        Assert.Contains(endpoints, e => e.Port == a.ApiPort);
    }

    [Fact]
    public async Task Declined_request_trusts_nobody()
    {
        b.Pairing.IncomingRequested += (_, request) => b.Pairing.Decline(request.Id);

        PairingOutcome outcome = await a.Pairing.RequestAsync(b.Id, null, _ => { }, Ct);

        Assert.Equal(PairingOutcome.Declined, outcome);
        Assert.Equal(TrustLevel.Unknown, a.Trust.GetLevel(b.Id));
        Assert.Equal(TrustLevel.Unknown, b.Trust.GetLevel(a.Id));
    }

    [Fact]
    public async Task Blocking_a_request_blocks_the_requester()
    {
        b.Pairing.IncomingRequested += (_, request) => _ = b.Pairing.BlockAsync(request.Id);

        PairingOutcome outcome = await a.Pairing.RequestAsync(b.Id, null, _ => { }, Ct);

        Assert.Equal(PairingOutcome.Declined, outcome);
        await WaitUntil(() => b.Trust.GetLevel(a.Id) == TrustLevel.Blocked);
    }

    [Fact]
    public async Task Requester_cancelling_closes_the_request_on_the_other_side()
    {
        var closed = new TaskCompletionSource<string>();
        b.Pairing.IncomingClosed += (_, id) => closed.TrySetResult(id);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        PairingOutcome outcome = await a.Pairing.RequestAsync(b.Id, null, _ => cancel.CancelAfter(TimeSpan.FromMilliseconds(300)), cancel.Token);

        Assert.Equal(PairingOutcome.Cancelled, outcome);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Disconnect_forgets_on_both_sides()
    {
        b.Pairing.IncomingRequested += (_, request) => b.Pairing.Accept(request.Id);
        await a.Pairing.RequestAsync(b.Id, null, _ => { }, Ct);
        await WaitUntil(() => b.Trust.GetLevel(a.Id) == TrustLevel.Trusted);

        await a.Pairing.DisconnectAsync(b.Id, Ct);

        Assert.Equal(TrustLevel.Unknown, a.Trust.GetLevel(b.Id));
        Assert.Equal(TrustLevel.Unknown, b.Trust.GetLevel(a.Id));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 5s.");
            await Task.Delay(50, Ct);
        }
    }
}
