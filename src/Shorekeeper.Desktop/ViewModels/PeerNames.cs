using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>How a peer is named in the UI (<see cref="PeerNaming"/>: told apart from anyone with the same name).</summary>
public sealed class PeerNames(TrustStore trust, PeerDirectory directory, PeerNaming naming, IDialogs dialogs)
{
    /// <summary>A name may have changed: a contact renamed itself, or we gave someone a name of our own.</summary>
    public event EventHandler? Changed
    {
        add => trust.Changed += value;
        remove => trust.Changed -= value;
    }

    public string Of(DeviceId id) => naming.Of(id);

    /// <param name="fallback">The name to use when nothing else knows it, e.g. its name in a group.</param>
    public string Of(DeviceId id, string fallback) => naming.Of(id, fallback);

    public bool IsOnline(DeviceId id) => directory.Snapshot().Any(p => p.DeviceId == id && p.State == PeerState.Online);

    /// <summary>A phone, as it says on the network or, while offline, as it last said (contacts only).</summary>
    public bool IsPhone(DeviceId id) =>
        Live(id) is { } live ? live.Os.Equals("android", StringComparison.OrdinalIgnoreCase) : trust.Get(id)?.IsPhone == true;

    /// <summary>"Đặt tên gợi nhớ…" for anyone, contact or not; <paramref name="knownAs"/> is their name when we have none.</summary>
    public async Task AskAliasAsync(DeviceId id, string knownAs)
    {
        string current = trust.GetAlias(id) ?? trust.Get(id)?.DisplayName ?? knownAs;
        if (await dialogs.PromptAsync(L.T("Đặt tên gợi nhớ"), L.F("Tên gợi nhớ cho {0}:", naming.Of(id, knownAs)), current) is { } alias)
        {
            await trust.SetAliasAsync(id, alias);
        }
    }

    /// <summary>The peer as seen on the network right now; null while it is not.</summary>
    public PeerInfo? Live(DeviceId id) => directory.Snapshot().FirstOrDefault(p => p.DeviceId == id);
}
