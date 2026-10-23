using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

/// <summary>
/// The audit log in the Windows "Application" log, source <see cref="Source"/>: kept and forwarded like any other
/// event, and only an administrator can clear it. The MSI registers the source (registering needs administrator rights).
/// </summary>
public sealed class EventLogAuditSink(ILogger<EventLogAuditSink> logger) : IAuditSink
{
    public const string Source = "Shorekeeper";

    /// <summary>An event's text holds at most 31,839 characters.</summary>
    private const int MaxMessage = 31_000;

    private int warned;

    public void Write(int eventId, string message)
    {
        try
        {
            EventLog.WriteEntry(Source, message.Length > MaxMessage ? message[..MaxMessage] : message, EventLogEntryType.Information, eventId);
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or Win32Exception or ArgumentException)
        {
            // Not installed by the MSI: the source is missing, and creating it is refused.
            if (Interlocked.Exchange(ref warned, 1) == 0)
            {
                logger.LogError(ex, "Audit log: cannot write to the Windows Event Log (is event source {Source} registered?)", Source);
            }
        }
    }
}
