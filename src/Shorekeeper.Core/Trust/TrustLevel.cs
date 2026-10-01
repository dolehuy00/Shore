namespace Shorekeeper.Core.Trust;

/// <summary>Relationship with a peer (docs/04-identity-security.md §3.1). Stored as an integer in <c>Peers.TrustLevel</c>.</summary>
public enum TrustLevel
{
    /// <summary>Only seen through discovery; may only ask to connect.</summary>
    Unknown = 0,

    /// <summary>Connected: one side asked, the other accepted.</summary>
    Trusted = 1,

    /// <summary>Every request is refused and the peer is hidden.</summary>
    Blocked = 2,
}
