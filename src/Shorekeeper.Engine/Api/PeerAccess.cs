using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Identity;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Api;

public enum RequiredAccess
{
    /// <summary>Any peer that is not blocked: only for asking permission (hello, connect request).</summary>
    AnyPeer,

    /// <summary>Peers the user has connected with.</summary>
    Trusted,

    /// <summary>Contacts, or peers in a group with us; the handler checks the specific group (docs/04-identity-security.md §4.2).</summary>
    TrustedOrGroupMember,
}

/// <summary>Endpoint metadata declaring who may call it. Endpoints without it are refused (default deny).</summary>
public sealed record AccessRequirement(RequiredAccess Level);

public static class PeerAccess
{
    private const string CallerKey = "Shorekeeper.Caller";

    public static TBuilder AllowAnyPeer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AccessRequirement(RequiredAccess.AnyPeer));

    public static TBuilder RequireTrustedPeer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AccessRequirement(RequiredAccess.Trusted));

    public static TBuilder RequireTrustedOrGroupMember<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AccessRequirement(RequiredAccess.TrustedOrGroupMember));

    /// <summary>DeviceId of the peer making the request, taken from its TLS client certificate.</summary>
    public static DeviceId GetCaller(this HttpContext context) => (DeviceId)context.Items[CallerKey]!;

    /// <summary>
    /// Identifies the caller from its client certificate and enforces <see cref="AccessRequirement"/>
    /// (docs/04-identity-security.md §3). Blocked peers and unknown endpoints get the same answer as
    /// untrusted peers so nothing is revealed.
    /// </summary>
    public static IApplicationBuilder UsePeerAccess(this IApplicationBuilder app, TrustStore trust, GroupStore groups) =>
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (context.Connection.ClientCertificate is not { } certificate)
            {
                await ApiResults.Problem(StatusCodes.Status401Unauthorized, ApiErrorCodes.NotTrusted).ExecuteAsync(context);
                return;
            }

            DeviceId caller = DeviceIdentity.FromCertificate(certificate);
            context.Items[CallerKey] = caller;

            AccessRequirement? requirement = context.GetEndpoint()?.Metadata.GetMetadata<AccessRequirement>();
            TrustLevel level = trust.GetLevel(caller);
            bool allowed = level != TrustLevel.Blocked && requirement?.Level switch
            {
                RequiredAccess.AnyPeer => true,
                RequiredAccess.Trusted => level == TrustLevel.Trusted,
                RequiredAccess.TrustedOrGroupMember => level == TrustLevel.Trusted || groups.SharesGroup(caller),
                _ => false,
            };

            if (!allowed)
            {
                await ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.NotTrusted).ExecuteAsync(context);
                return;
            }

            await next(context);
        });
}

public static class ApiResults
{
    public static IResult Problem(int status, string code) =>
        Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });
}
