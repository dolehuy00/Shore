using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Api;

/// <summary>
/// HTTP endpoints of the peer API (docs/05-protocol.md §4). Every endpoint must declare
/// <see cref="PeerAccess.AllowAnyPeer"/> or <see cref="PeerAccess.RequireTrustedPeer"/>; anything else is refused.
/// </summary>
internal static class PeerApiEndpoints
{
    public static void Map(IEndpointRouteBuilder app, IServiceProvider services)
    {
        var device = services.GetRequiredService<LocalDevice>();
        var server = services.GetRequiredService<ApiServer>();
        var pairing = services.GetRequiredService<PairingService>();
        var trust = services.GetRequiredService<TrustStore>();

        RouteGroupBuilder api = app.MapGroup(PeerClient.ApiBasePath.TrimEnd('/'));

        api.MapGet("/hello", () => new HelloResponse(
                device.DeviceId.Value, device.DisplayName, LocalDevice.HostName, LocalDevice.Os, LocalDevice.AppVersion, [1], server.Port))
            .AllowAnyPeer();

        api.MapPost("/pairing/request", (HttpContext context, PairingRequestBody body) =>
            {
                var (result, response) = pairing.HandleRequest(context.GetCaller(), context.Connection.RemoteIpAddress!, body);
                return result switch
                {
                    IncomingRequestResult.Accepted => Results.Json(response, statusCode: StatusCodes.Status202Accepted),
                    IncomingRequestResult.RateLimited => ApiResults.Problem(StatusCodes.Status429TooManyRequests, ApiErrorCodes.RateLimited),
                    _ => ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest),
                };
            })
            .AllowAnyPeer();

        // Only the requester itself gets an answer; anyone else sees "expired".
        api.MapGet("/pairing/{id}/decision", async (HttpContext context, string id, CancellationToken cancellationToken) =>
                new PairingDecisionResponse(await pairing.WaitDecisionAsync(context.GetCaller(), id, PairingService.DecisionWait, cancellationToken)))
            .AllowAnyPeer();

        api.MapDelete("/pairing/{id}", (HttpContext context, string id) =>
            {
                pairing.CancelByRequester(context.GetCaller(), id);
                return Results.NoContent();
            })
            .AllowAnyPeer();

        api.MapPost("/pairing/revoke", async (HttpContext context) =>
            {
                DeviceId caller = context.GetCaller();
                await trust.ForgetAsync(caller);
                return Results.NoContent();
            })
            .RequireTrustedPeer();
    }
}
