using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Transfers;
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
        var offers = services.GetRequiredService<OfferService>();
        var inbox = services.GetRequiredService<InboxService>();

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

        // ───────────── Sending files (docs/05-protocol.md §4.2, §4.3) ─────────────

        // Recipient side: a contact offers us files.
        api.MapPost("/inbox/offers", async (HttpContext context, OfferManifest manifest) =>
                await inbox.HandleOfferAsync(context.GetCaller(), manifest)
                    ? Results.Accepted()
                    : ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest))
            .RequireTrustedPeer();

        api.MapPost("/inbox/offers/{offerId}/withdrawn", async (HttpContext context, string offerId) =>
            {
                await inbox.HandleWithdrawnAsync(context.GetCaller(), offerId);
                return Results.NoContent();
            })
            .RequireTrustedPeer();

        // Sender side: recipients pull the data. Each handler also checks the caller is a recipient.
        api.MapGet("/offers/{offerId}/files/{fileId}", (HttpContext context, string offerId, string fileId) =>
                offers.ServeFile(context.GetCaller(), offerId, fileId))
            .RequireTrustedPeer();

        api.MapGet("/offers/{offerId}/files/{fileId}/hash", (HttpContext context, string offerId, string fileId, CancellationToken cancellationToken) =>
                offers.GetHashAsync(context.GetCaller(), offerId, fileId, cancellationToken))
            .RequireTrustedPeer();

        api.MapPost("/offers/{offerId}/receipts", (HttpContext context, string offerId, OfferReceipt receipt) =>
                offers.HandleReceiptAsync(context.GetCaller(), offerId, receipt))
            .RequireTrustedPeer();
    }
}
