using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
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
        var pex = services.GetRequiredService<PexService>();
        var bridge = services.GetRequiredService<BridgeRegistry>();
        var groupStore = services.GetRequiredService<GroupStore>();
        var groups = services.GetRequiredService<GroupService>();

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

        // PEX: only contacts may read who we hear (docs/03-discovery-presence.md §5).
        api.MapGet("/peers/known", (HttpContext context) => pex.GetKnown(context.GetCaller()))
            .RequireTrustedPeer();

        // ───────────── Bridge (docs/05-protocol.md §4.6): public presence only, so any peer ─────────────

        api.MapPost("/bridge/register", (HttpContext context, BridgeRegistration body) =>
            {
                var (result, response) = bridge.Register(context.GetCaller(), context.Connection.RemoteIpAddress!, body);
                return result switch
                {
                    BridgeRegisterResult.Registered => Results.Ok(response),
                    BridgeRegisterResult.Disabled => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound),
                    BridgeRegisterResult.Full => ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.Busy),
                    _ => ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest),
                };
            })
            .AllowAnyPeer();

        api.MapDelete("/bridge/register", (HttpContext context) =>
            {
                bridge.Unregister(context.GetCaller());
                return Results.NoContent();
            })
            .AllowAnyPeer();

        api.MapGet("/bridge/peers", async (long? since, int? wait, CancellationToken cancellationToken) =>
                await bridge.GetPeersAsync(
                        since ?? 0, TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, (int)BridgeRegistry.MaxWait.TotalSeconds)), cancellationToken)
                    is { } response
                    ? Results.Ok(response)
                    : ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound))
            .AllowAnyPeer();

        // ───────────── Sending files (docs/05-protocol.md §4.2, §4.3) ─────────────

        // Recipient side: a contact, or a fellow member of the offer's group, offers us files.
        api.MapPost("/inbox/offers", async (HttpContext context, OfferManifest manifest) =>
                !groupStore.CanExchangeFiles(context.GetCaller(), manifest.GroupId)
                    ? ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.NotTrusted)
                    : await inbox.HandleOfferAsync(context.GetCaller(), manifest)
                        ? Results.Accepted()
                        : ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest))
            .RequireTrustedOrGroupMember();

        api.MapPost("/inbox/offers/{offerId}/withdrawn", async (HttpContext context, string offerId) =>
            {
                await inbox.HandleWithdrawnAsync(context.GetCaller(), offerId);
                return Results.NoContent();
            })
            .RequireTrustedOrGroupMember();

        // Sender side: recipients pull the data. Each handler also checks the caller is a recipient
        // and still a contact or member of the offer's group.
        api.MapGet("/offers/{offerId}/files/{fileId}", (HttpContext context, string offerId, string fileId) =>
                offers.ServeFile(context.GetCaller(), offerId, fileId))
            .RequireTrustedOrGroupMember();

        api.MapGet("/offers/{offerId}/files/{fileId}/hash", (HttpContext context, string offerId, string fileId, CancellationToken cancellationToken) =>
                offers.GetHashAsync(context.GetCaller(), offerId, fileId, cancellationToken))
            .RequireTrustedOrGroupMember();

        api.MapPost("/offers/{offerId}/receipts", (HttpContext context, string offerId, OfferReceipt receipt) =>
                offers.HandleReceiptAsync(context.GetCaller(), offerId, receipt))
            .RequireTrustedOrGroupMember();

        // ───────────── Groups (docs/05-protocol.md §4.4): handlers decide, unknown peers may only ask ─────────────

        // On the Host: someone asks to join (or accepts our invitation).
        api.MapPost("/groups/{groupId}/join-requests", async (HttpContext context, string groupId, GroupJoinRequestBody body) =>
                await groups.HandleJoinRequestAsync(context.GetCaller(), groupId, body) switch
                {
                    JoinRequestResult.Pending => Results.Json(new GroupJoinResponse(GroupStatus.Pending), statusCode: StatusCodes.Status202Accepted),
                    JoinRequestResult.Member => Results.Ok(new GroupJoinResponse(GroupStatus.Member)),
                    JoinRequestResult.RateLimited => ApiResults.Problem(StatusCodes.Status429TooManyRequests, ApiErrorCodes.RateLimited),
                    JoinRequestResult.Full => ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.Busy),
                    JoinRequestResult.NotFound => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound),
                    _ => ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest),
                })
            .AllowAnyPeer();

        // On the Host: members long-poll the member list; requesters the decision. Anyone else: 404.
        api.MapGet("/groups/{groupId}/state", async (HttpContext context, string groupId, long? since, int? wait, CancellationToken cancellationToken) =>
                await groups.GetStateAsync(
                        context.GetCaller(), groupId, since ?? 0,
                        TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, (int)GroupService.MaxWait.TotalSeconds)), cancellationToken)
                    is { } state
                    ? Results.Ok(state)
                    : ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound))
            .AllowAnyPeer();

        api.MapPost("/groups/{groupId}/leave", async (HttpContext context, string groupId) =>
            {
                await groups.HandleLeaveAsync(context.GetCaller(), groupId);
                return Results.NoContent();
            })
            .AllowAnyPeer();

        // On the invitee: a Host invites us; the user decides.
        api.MapPost("/groups/invitations", (HttpContext context, GroupInvitationBody body) =>
                groups.HandleInvitation(context.GetCaller(), body) switch
                {
                    JoinRequestResult.Pending or JoinRequestResult.Member => Results.Accepted(),
                    JoinRequestResult.RateLimited => ApiResults.Problem(StatusCodes.Status429TooManyRequests, ApiErrorCodes.RateLimited),
                    JoinRequestResult.NotFound => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound),
                    _ => ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest),
                })
            .AllowAnyPeer();
    }
}
