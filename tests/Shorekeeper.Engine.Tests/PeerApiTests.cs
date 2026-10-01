using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

/// <summary>Access control of the peer API: default deny, unknown peers may only ask (docs/04-identity-security.md §3).</summary>
public sealed class PeerApiTests : IAsyncLifetime
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
    public async Task Unknown_peer_can_say_hello()
    {
        using HttpResponseMessage response = await Send(HttpMethod.Get, "hello");

        HelloResponse hello = await PeerClient.ReadAsync<HelloResponse>(response, Ct);
        Assert.Equal(b.Id.Value, hello.DeviceId);
        Assert.Equal("B", hello.Name);
        Assert.Equal(b.ApiPort, hello.Port);
    }

    [Fact]
    public async Task Unknown_peer_can_ask_to_connect()
    {
        using HttpResponseMessage response = await Send(HttpMethod.Post, "pairing/request", PairingBody());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "pairing/revoke")]
    [InlineData("GET", "peers/known")] // not implemented yet: must still be refused, not 404
    [InlineData("GET", "anything")]
    public async Task Unknown_peer_is_refused_everything_else(string method, string path)
    {
        using HttpResponseMessage response = await Send(new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        PeerApiException error = await Assert.ThrowsAsync<PeerApiException>(() => PeerClient.ReadAsync<object>(response, Ct));
        Assert.Equal(ApiErrorCodes.NotTrusted, error.Code);
    }

    [Fact]
    public async Task Trusted_peer_may_call_trusted_endpoints()
    {
        await b.Trust.SetTrustedAsync(a.Id, "A", "HOST-A", Ct);

        using HttpResponseMessage response = await Send(HttpMethod.Post, "pairing/revoke");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(TrustLevel.Unknown, b.Trust.GetLevel(a.Id));
    }

    [Fact]
    public async Task Blocked_peer_cannot_even_say_hello()
    {
        await b.Trust.BlockAsync(a.Id, "A", "HOST-A", Ct);

        using HttpResponseMessage response = await Send(HttpMethod.Get, "hello");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Connect_requests_are_rate_limited_per_peer()
    {
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage ok = await Send(HttpMethod.Post, "pairing/request", PairingBody());
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        using HttpResponseMessage limited = await Send(HttpMethod.Post, "pairing/request", PairingBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Invalid_connect_request_is_rejected()
    {
        using HttpResponseMessage response = await Send(HttpMethod.Post, "pairing/request", PairingBody() with { Nonce = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Client_refuses_a_server_with_another_identity()
    {
        await using TestNode c = await TestNode.StartAsync("C");
        // A believes C's address belongs to B: the TLS check must fail rather than talk to C.
        a.Directory.Remove(b.Id);
        a.Directory.Observe(
            new Core.Discovery.PresencePacket { Type = "heartbeat", Id = b.Id.Value, Port = c.ApiPort },
            IPAddress.Loopback);

        await Assert.ThrowsAsync<PeerUnreachableException>(() => Send(HttpMethod.Get, "hello"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null) =>
        a.Client.SendAsync(
            b.Id,
            baseUri => new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = body is null ? null : JsonContent.Create(body) },
            TimeSpan.FromSeconds(10),
            Ct);

    private static PairingRequestBody PairingBody() => new(
        Guid.NewGuid().ToString("N"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(PairingCode.NonceSize)), "A", "HOST-A", 47471, null);
}
