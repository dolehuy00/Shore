using System.Net;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Groups;
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

/// <summary>
/// The peer API without an HTTP server: routes, who may call each one, and the handlers (docs/05-protocol.md §4).
/// Every route declares a <see cref="RequiredAccess"/>; requests matching no route are refused like untrusted ones.
/// </summary>
public sealed class PeerApi(TrustStore trust, GroupStore groups)
{
    private readonly List<PeerRoute> routes = [];

    public void Map(string method, string template, RequiredAccess access, Func<PeerRequest, Task<PeerResponse>> handler) =>
        routes.Add(new PeerRoute(method, template.Trim('/').Split('/'), access, handler));

    public void Map(string method, string template, RequiredAccess access, Func<PeerRequest, PeerResponse> handler) =>
        Map(method, template, access, request => Task.FromResult(handler(request)));

    /// <summary>
    /// Finds the route and enforces its access level (docs/04-identity-security.md §3). Blocked peers, unknown
    /// routes and peers without the right relationship all get the same answer, so nothing is revealed.
    /// </summary>
    public Task<PeerResponse> HandleAsync(PeerRequest request)
    {
        string[] segments = request.Path.Trim('/').Split('/');
        foreach (PeerRoute route in routes)
        {
            if (route.Method == request.Method && route.TryMatch(segments, out Dictionary<string, string> values))
            {
                if (!IsAllowed(request, route.Access))
                {
                    break;
                }

                request.RouteValues = values;
                return route.Handler(request);
            }
        }

        return Task.FromResult(PeerResponse.Problem(HttpStatusCode.Forbidden, ApiErrorCodes.NotTrusted));
    }

    /// <summary>A contact, or in a group with us: may send large bodies (offers, voice messages). Anyone else only asks.</summary>
    public bool IsKnown(DeviceId caller) =>
        trust.GetLevel(caller) == TrustLevel.Trusted || groups.SharesGroup(caller);

    private bool IsAllowed(PeerRequest request, RequiredAccess access)
    {
        TrustLevel level = trust.GetLevel(request.Caller);
        return level != TrustLevel.Blocked && access switch
        {
            RequiredAccess.AnyPeer => true,
            RequiredAccess.Trusted => level == TrustLevel.Trusted,
            RequiredAccess.TrustedOrGroupMember => level == TrustLevel.Trusted || groups.SharesGroup(request.Caller),
            _ => false,
        };
    }

    private sealed record PeerRoute(string Method, string[] Segments, RequiredAccess Access, Func<PeerRequest, Task<PeerResponse>> Handler)
    {
        public bool TryMatch(string[] path, out Dictionary<string, string> values)
        {
            values = [];
            if (path.Length != Segments.Length)
            {
                return false;
            }

            for (int i = 0; i < path.Length; i++)
            {
                string segment = Segments[i];
                if (segment.StartsWith('{'))
                {
                    values[segment[1..^1]] = Uri.UnescapeDataString(path[i]);
                }
                else if (!string.Equals(segment, path[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
