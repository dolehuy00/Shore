using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>How a peer is named in the UI: the alias or contact name, else its presence name.</summary>
public sealed class PeerNames(TrustStore trust, PeerDirectory directory)
{
    public string Of(DeviceId id) =>
        trust.Get(id)?.ShownName
        ?? directory.Snapshot().FirstOrDefault(p => p.DeviceId == id)?.DisplayName
        ?? id.ShortForm;

    public bool IsOnline(DeviceId id) => directory.Snapshot().Any(p => p.DeviceId == id && p.State == PeerState.Online);
}
