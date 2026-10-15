using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// The signed-in user's entry in Active Directory: name, department, title, mail and <c>thumbnailPhoto</c>
/// (docs/13-active-directory.md §2). Computers outside a domain are recognized locally, without any network call.
/// </summary>
public sealed partial class ActiveDirectoryInfo(ILogger<ActiveDirectoryInfo> logger) : IDirectoryInfo
{
    private const int NetSetupDomainName = 3;

    public DirectoryProfile? GetCurrentUser()
    {
        if (!IsDomainJoined() || WindowsIdentity.GetCurrent().User is not { } sid)
        {
            return null;
        }

        try
        {
            using var context = new PrincipalContext(ContextType.Domain);
            using UserPrincipal? user = UserPrincipal.FindByIdentity(context, IdentityType.Sid, sid.Value);
            if (user is null)
            {
                // A local account on a domain-joined computer.
                return null;
            }

            var entry = (DirectoryEntry)user.GetUnderlyingObject();
            string name = user.DisplayName is { Length: > 0 } displayName ? displayName : user.Name;
            return new DirectoryProfile(
                name,
                entry.Properties["department"].Value as string,
                entry.Properties["title"].Value as string,
                user.EmailAddress,
                entry.Properties["thumbnailPhoto"].Value as byte[]);
        }
        catch (Exception ex) when (ex is PrincipalException or COMException or InvalidOperationException)
        {
            // Typically: away from the office, no domain controller reachable.
            logger.LogInformation(ex, "Active Directory is not reachable");
            return null;
        }
    }

    private static bool IsDomainJoined()
    {
        if (NetGetJoinInformation(null, out IntPtr name, out int status) != 0)
        {
            return false;
        }

        _ = NetApiBufferFree(name);
        return status == NetSetupDomainName;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetGetJoinInformation(string? server, out IntPtr name, out int status);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);
}
