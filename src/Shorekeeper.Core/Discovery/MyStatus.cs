namespace Shorekeeper.Core.Discovery;

/// <summary>The status the user picks for this device (docs/03-discovery-presence.md §12).</summary>
public enum MyStatus
{
    Online,
    Busy,

    /// <summary>No presence is broadcast; this device still sees others.</summary>
    Hidden,
}
