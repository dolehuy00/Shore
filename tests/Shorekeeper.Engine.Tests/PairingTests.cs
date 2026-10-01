using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

/// <summary>"Kết nối" end to end between two real engines (docs/04-identity-security.md §4).</summary>
public sealed class PairingTests : IAsyncLifetime
{
    private TestNode a = null!;
    private TestNode b = null!;

    public async ValueTask InitializeAsync()
    {
        a = await TestNode.StartAsync("A");
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
