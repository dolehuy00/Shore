namespace Shorekeeper.Core.Platform;

/// <summary>
/// Where the audit log goes when IT turns it on (policy <c>AuditLog</c>, docs/11-windows-deployment.md §9):
/// the Windows Event Log, which only an administrator can clear.
/// </summary>
public interface IAuditSink
{
    /// <param name="eventId">What happened, the same number on every computer.</param>
    /// <param name="message">One line of JSON with the details.</param>
    void Write(int eventId, string message);
}

/// <summary>Nowhere to write (Android, tests that do not look).</summary>
public sealed class NullAuditSink : IAuditSink
{
    public static NullAuditSink Instance { get; } = new();

    public void Write(int eventId, string message)
    {
    }
}
