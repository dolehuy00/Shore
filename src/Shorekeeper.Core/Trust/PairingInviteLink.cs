using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using Shorekeeper.Core.Identity;

namespace Shorekeeper.Core.Trust;

/// <summary>
/// "Kết nối bằng mã QR" (docs/04-identity-security.md §4.4): what the computer's QR code holds. Who it is (the phone
/// checks the device that answers is that one), where to reach it, and a one-time invitation that lets the phone connect
/// without the code being compared, since whoever scans it is looking at the computer's screen.
/// <c>shorekeeper://pair?id=&lt;device id&gt;&amp;a=&lt;ip:port&gt;,&lt;ip:port&gt;&amp;k=&lt;invitation&gt;</c>
/// </summary>
public sealed record PairingInviteLink(DeviceId DeviceId, IReadOnlyList<IPEndPoint> Endpoints, string Invite)
{
    public const int MaxEndpoints = 8;
    public const int MaxInviteLength = 64;
    private const string Prefix = "shorekeeper://pair?";

    public string Format() =>
        $"{Prefix}id={DeviceId.Value}&a={string.Join(',', Endpoints.Take(MaxEndpoints).Select(e => e.ToString()))}&k={Uri.EscapeDataString(Invite)}";

    public static bool TryParse(string? text, [NotNullWhen(true)] out PairingInviteLink? link)
    {
        link = null;
        if (text is null || !text.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Dictionary<string, string> fields = [];
        foreach (string part in text.Trim()[Prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals > 0)
            {
                fields[part[..equals]] = Uri.UnescapeDataString(part[(equals + 1)..]);
            }
        }

        if (!fields.TryGetValue("id", out string? id) || !DeviceId.TryParse(id, out DeviceId deviceId)
            || !fields.TryGetValue("k", out string? invite) || invite.Length is 0 or > MaxInviteLength
            || !fields.TryGetValue("a", out string? addresses))
        {
            return false;
        }

        List<IPEndPoint> endpoints = [];
        foreach (string address in addresses.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(MaxEndpoints))
        {
            if (IPEndPoint.TryParse(address, out IPEndPoint? endpoint) && endpoint.Port > 0)
            {
                endpoints.Add(endpoint);
            }
        }

        if (endpoints.Count == 0)
        {
            return false;
        }

        link = new PairingInviteLink(deviceId, endpoints, invite);
        return true;
    }

    // Logs never show the invitation.
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{DeviceId.ShortForm} ({Endpoints.Count} addresses)");
}
