using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>How a peer is named in the UI (<see cref="PeerNaming"/>: told apart from anyone with the same name).</summary>
public sealed class PeerNames(TrustStore trust, PeerDirectory directory, PeerNaming naming)
{
    public string Of(DeviceId id) => naming.Of(id);

    /// <param name="fallback">The name to use when nothing else knows it, e.g. its name in a group.</param>
    public string Of(DeviceId id, string fallback) => naming.Of(id, fallback);

    public bool IsOnline(DeviceId id) => directory.Snapshot().Any(p => p.DeviceId == id && p.State == PeerState.Online);

    /// <summary>A phone, as it says on the network or, while offline, as it last said (contacts only).</summary>
    public bool IsPhone(DeviceId id) =>
        Live(id) is { } live ? live.Os.Equals("android", StringComparison.OrdinalIgnoreCase) : trust.Get(id)?.IsPhone == true;

    /// <summary>The peer as seen on the network right now; null while it is not.</summary>
    public PeerInfo? Live(DeviceId id) => directory.Snapshot().FirstOrDefault(p => p.DeviceId == id);
}
