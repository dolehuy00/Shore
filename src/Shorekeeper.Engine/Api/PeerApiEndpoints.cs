using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Audio;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Api;

/// <summary>Routes of the peer API (docs/05-protocol.md §4). Every route declares who may call it.</summary>
public static class PeerApiEndpoints
{
    public static PeerApi Create(IServiceProvider services)
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
        var profile = services.GetRequiredService<DirectoryProfileService>();
        var audio = services.GetRequiredService<AudioService>();
        var chat = services.GetRequiredService<ChatService>();
        var groupChat = services.GetRequiredService<GroupChatService>();
        var api = new PeerApi(trust, groupStore);

        api.Map("GET", "/hello", RequiredAccess.AnyPeer, _ => PeerResponse.Json(new HelloResponse(
            device.DeviceId.Value, device.DisplayName, LocalDevice.HostName, LocalDevice.Os, LocalDevice.AppVersion, [1], server.Port,
            profile.Department, profile.AvatarTag)));

        // The picture from the company directory, public like the name (docs/13-active-directory.md §2).
        api.Map("GET", "/hello/avatar", RequiredAccess.AnyPeer, _ => profile.Avatar is { } photo
            ? PeerResponse.File(new MemoryStream(photo, writable: false), AvatarContentType(photo))
            : NotFound());

        api.Map("POST", "/pairing/request", RequiredAccess.AnyPeer, async request =>
        {
            if (await request.ReadJsonAsync<PairingRequestBody>() is not { } body)
            {
                return Invalid();
            }

            var (result, response) = pairing.HandleRequest(request.Caller, request.RemoteAddress, body);
            return result switch
            {
                IncomingRequestResult.Accepted => PeerResponse.Json(response, HttpStatusCode.Accepted),
                IncomingRequestResult.RateLimited => PeerResponse.Problem(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited),
                _ => Invalid(),
            };
        });

        // Only the requester itself gets an answer; anyone else sees "expired".
        api.Map("GET", "/pairing/{id}/decision", RequiredAccess.AnyPeer, async request =>
            PeerResponse.Json(new PairingDecisionResponse(
                await pairing.WaitDecisionAsync(request.Caller, request.Route("id"), PairingService.DecisionWait, request.Aborted))));

        api.Map("DELETE", "/pairing/{id}", RequiredAccess.AnyPeer, request =>
        {
            pairing.CancelByRequester(request.Caller, request.Route("id"));
            return NoContent();
        });

        api.Map("POST", "/pairing/revoke", RequiredAccess.Trusted, async request =>
        {
            await trust.ForgetAsync(request.Caller);
            return NoContent();
        });

        // PEX: only contacts may read who we hear (docs/03-discovery-presence.md §5).
        api.Map("GET", "/peers/known", RequiredAccess.Trusted, request => PeerResponse.Json(pex.GetKnown(request.Caller)));

        // ───────────── Bridge (docs/05-protocol.md §4.6): public presence only, so any peer ─────────────

        api.Map("POST", "/bridge/register", RequiredAccess.AnyPeer, async request =>
        {
            if (await request.ReadJsonAsync<BridgeRegistration>() is not { } body)
            {
                return Invalid();
            }

            var (result, response) = bridge.Register(request.Caller, request.RemoteAddress, body);
            return result switch
            {
                BridgeRegisterResult.Registered => PeerResponse.Json(response),
                BridgeRegisterResult.Disabled => NotFound(),
                BridgeRegisterResult.Full => Busy(),
                _ => Invalid(),
            };
        });

        api.Map("DELETE", "/bridge/register", RequiredAccess.AnyPeer, request =>
        {
            bridge.Unregister(request.Caller);
            return NoContent();
        });

        api.Map("GET", "/bridge/peers", RequiredAccess.AnyPeer, async request =>
            await bridge.GetPeersAsync(
                    request.QueryInt64("since") ?? 0, Wait(request, BridgeRegistry.MaxWait), request.Aborted)
                is { } response
                ? PeerResponse.Json(response)
                : NotFound());

        // ───────────── Sending files (docs/05-protocol.md §4.2, §4.3) ─────────────

        // Recipient side: a contact, or a fellow member of the offer's group, offers us files.
        api.Map("POST", "/inbox/offers", RequiredAccess.TrustedOrGroupMember, async request =>
            await request.ReadJsonAsync<OfferManifest>() is not { } manifest ? Invalid()
            : !groupStore.CanExchangeFiles(request.Caller, manifest.GroupId) ? PeerResponse.Problem(HttpStatusCode.Forbidden, ApiErrorCodes.NotTrusted)
            : await inbox.HandleOfferAsync(request.Caller, manifest) ? PeerResponse.Empty(HttpStatusCode.Accepted)
            : Invalid());

        api.Map("POST", "/inbox/offers/{offerId}/withdrawn", RequiredAccess.TrustedOrGroupMember, async request =>
        {
            await inbox.HandleWithdrawnAsync(request.Caller, request.Route("offerId"));
            return NoContent();
        });

        // Sender side: recipients pull the data. Each handler also checks the caller is a recipient
        // and still a contact or member of the offer's group.
        api.Map("GET", "/offers/{offerId}/files/{fileId}", RequiredAccess.TrustedOrGroupMember, request =>
            offers.ServeFile(request.Caller, request.Route("offerId"), request.Route("fileId")));

        api.Map("GET", "/offers/{offerId}/files/{fileId}/hash", RequiredAccess.TrustedOrGroupMember, request =>
            offers.GetHashAsync(request.Caller, request.Route("offerId"), request.Route("fileId"), request.Aborted));

        api.Map("POST", "/offers/{offerId}/receipts", RequiredAccess.TrustedOrGroupMember, async request =>
            await request.ReadJsonAsync<OfferReceipt>() is { } receipt
                ? await offers.HandleReceiptAsync(request.Caller, request.Route("offerId"), receipt)
                : Invalid());

        // ───────────── Groups (docs/05-protocol.md §4.4): handlers decide, unknown peers may only ask ─────────────

        // On the Host: someone asks to join (or accepts our invitation).
        api.Map("POST", "/groups/{groupId}/join-requests", RequiredAccess.AnyPeer, async request =>
            await request.ReadJsonAsync<GroupJoinRequestBody>() is not { } body ? Invalid()
            : await groups.HandleJoinRequestAsync(request.Caller, request.Route("groupId"), body) switch
            {
                JoinRequestResult.Pending => PeerResponse.Json(new GroupJoinResponse(GroupStatus.Pending), HttpStatusCode.Accepted),
                JoinRequestResult.Member => PeerResponse.Json(new GroupJoinResponse(GroupStatus.Member)),
                JoinRequestResult.RateLimited => PeerResponse.Problem(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited),
                JoinRequestResult.Full => Busy(),
                JoinRequestResult.NotFound => NotFound(),
                _ => Invalid(),
            });

        // On the Host: members long-poll the member list; requesters the decision. Anyone else: 404.
        api.Map("GET", "/groups/{groupId}/state", RequiredAccess.AnyPeer, async request =>
            await groups.GetStateAsync(
                    request.Caller, request.Route("groupId"), request.QueryInt64("since") ?? 0, Wait(request, GroupService.MaxWait), request.Aborted)
                is { } state
                ? PeerResponse.Json(state)
                : NotFound());

        api.Map("POST", "/groups/{groupId}/leave", RequiredAccess.AnyPeer, async request =>
        {
            await groups.HandleLeaveAsync(request.Caller, request.Route("groupId"));
            return NoContent();
        });

        // On the invitee: a Host invites us; the user decides.
        api.Map("POST", "/groups/invitations", RequiredAccess.AnyPeer, async request =>
            await request.ReadJsonAsync<GroupInvitationBody>() is not { } body ? Invalid()
            : groups.HandleInvitation(request.Caller, body) switch
            {
                JoinRequestResult.Pending or JoinRequestResult.Member => PeerResponse.Empty(HttpStatusCode.Accepted),
                JoinRequestResult.RateLimited => PeerResponse.Problem(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited),
                JoinRequestResult.NotFound => NotFound(),
                _ => Invalid(),
            });

        // ───────────── Sound (docs/05-protocol.md §4.7): contacts only ─────────────

        // On the device playing its sound: a contact starts or stops listening.
        api.Map("POST", "/audio/listeners", RequiredAccess.Trusted, async request =>
            await request.ReadJsonAsync<AudioListenBody>() is not { } body ? Invalid()
            : audio.HandleJoin(request.Caller, request.RemoteAddress, body) switch
            {
                AudioJoinResult.Joined => PeerResponse.Json(new AudioJoinResponse(audio.CanBeControlled)),
                AudioJoinResult.NotPlaying => NotFound(),
                AudioJoinResult.Full => Busy(),
                AudioJoinResult.PasswordRequired => PeerResponse.Problem(HttpStatusCode.Forbidden, ApiErrorCodes.PasswordRequired),
                AudioJoinResult.WrongPassword => PeerResponse.Problem(HttpStatusCode.Forbidden, ApiErrorCodes.WrongPassword),
                AudioJoinResult.TooManyAttempts => PeerResponse.Problem(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited),
                _ => Invalid(),
            });

        // A listener presses a media key here ("Điều khiển"): only in the session it listens with.
        api.Map("POST", "/audio/listeners/{sessionId}/keys", RequiredAccess.Trusted, async request =>
            await request.ReadJsonAsync<AudioKeyBody>() is not { } body || !MediaKeyNames.TryParse(body.Key, out MediaKey key) ? Invalid()
            : audio.HandleKey(request.Caller, request.Route("sessionId"), key) ? NoContent()
            : NotFound());

        api.Map("DELETE", "/audio/listeners/{sessionId}", RequiredAccess.Trusted, request =>
        {
            audio.HandleLeave(request.Caller, request.Route("sessionId"));
            return NoContent();
        });

        // On the listener: the sound it listens to was turned off.
        api.Map("POST", "/audio/listening/{sessionId}/ended", RequiredAccess.Trusted, request =>
        {
            audio.HandleEnded(request.Caller, request.Route("sessionId"));
            return NoContent();
        });

        // ───────────── Messages (docs/15-chat.md §4): contacts only ─────────────

        api.Map("POST", "/chat/messages", RequiredAccess.Trusted, async request =>
            await request.ReadJsonAsync<ChatMessageBody>() is { } body ? await chat.HandleMessageAsync(request.Caller, body) : Invalid());

        api.Map("POST", "/chat/receipts", RequiredAccess.Trusted, async request =>
            await request.ReadJsonAsync<ChatReceiptBody>() is { } body ? await chat.HandleReceiptAsync(request.Caller, body) : Invalid());

        api.Map("POST", "/chat/recalls", RequiredAccess.Trusted, async request =>
            await request.ReadJsonAsync<ChatRecallBody>() is { } body ? await chat.HandleRecallAsync(request.Caller, body) : Invalid());

        // ───────────── Group messages (docs/16-group-chat.md §4): members of that group, contacts or not ─────────────

        // Open to any peer here: the handler answers "not_member" rather than "not_trusted", which an older version
        // gives for a route it does not have, so a sender can tell a member list behind ours from an old version.
        api.Map("POST", "/chat/groups/{groupId}/messages", RequiredAccess.AnyPeer, async request =>
            await request.ReadJsonAsync<GroupChatMessageBody>() is { } body
                ? await groupChat.HandleMessageAsync(request.Caller, request.Route("groupId"), body)
                : Invalid());

        api.Map("POST", "/chat/groups/{groupId}/receipts", RequiredAccess.AnyPeer, async request =>
            await request.ReadJsonAsync<ChatReceiptBody>() is { } body
                ? await groupChat.HandleReceiptAsync(request.Caller, request.Route("groupId"), body)
                : Invalid());

        api.Map("POST", "/chat/groups/{groupId}/recalls", RequiredAccess.AnyPeer, async request =>
            await request.ReadJsonAsync<GroupChatRecallBody>() is { } body
                ? await groupChat.HandleRecallAsync(request.Caller, request.Route("groupId"), body)
                : Invalid());

        // Members pull what they lack from each other (docs/16-group-chat.md §3.6).
        api.Map("GET", "/chat/groups/{groupId}/index", RequiredAccess.AnyPeer, async request =>
            await groupChat.HandleIndexAsync(request.Caller, request.Route("groupId"), Math.Max(request.QueryInt64("after") ?? 0, 0)));

        api.Map("POST", "/chat/groups/{groupId}/fetch", RequiredAccess.AnyPeer, async request =>
            await request.ReadJsonAsync<GroupChatFetchBody>() is { } body
                ? await groupChat.HandleFetchAsync(request.Caller, request.Route("groupId"), body)
                : Invalid());

        return api;
    }

    private static TimeSpan Wait(PeerRequest request, TimeSpan max) =>
        TimeSpan.FromSeconds(Math.Clamp(request.QueryInt64("wait") ?? 0, 0, (long)max.TotalSeconds));

    private static PeerResponse NoContent() => PeerResponse.Empty(HttpStatusCode.NoContent);

    private static PeerResponse NotFound() => PeerResponse.Problem(HttpStatusCode.NotFound, ApiErrorCodes.NotFound);

    private static PeerResponse Busy() => PeerResponse.Problem(HttpStatusCode.ServiceUnavailable, ApiErrorCodes.Busy);

    private static PeerResponse Invalid() => PeerResponse.Problem(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);

    /// <summary>Directory pictures are JPEG, sometimes PNG.</summary>
    private static string AvatarContentType(byte[] photo) =>
        photo is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] ? "image/png" : "image/jpeg";
}
