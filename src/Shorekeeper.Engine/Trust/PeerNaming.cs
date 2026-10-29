using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;

namespace Shorekeeper.Engine.Trust;

/// <summary>
/// How a device is named on screen: its alias or contact name, else what it announces, else its name in a group. When
/// someone else we know goes by the same name, " · " and its computer name follow, or its short id when that is the same
/// too, so two "Huy" can be told apart (docs/10-ux.md §2).
/// </summary>
public sealed class PeerNaming
{
    private readonly TrustStore trust;
    private readonly PeerDirectory directory;
    private readonly GroupStore groups;
    private readonly ShorekeeperEngine engine;
    private readonly Lock gate = new();

    /// <summary>Everyone we know, by the name they go by; built when first needed after a change.</summary>
    private Dictionary<string, List<(DeviceId Id, string? Host)>>? byName;

    public PeerNaming(TrustStore trust, PeerDirectory directory, GroupStore groups, ShorekeeperEngine engine)
    {
        this.trust = trust;
        this.directory = directory;
        this.groups = groups;
        this.engine = engine;
        trust.Changed += (_, _) => Forget();
        directory.Changed += (_, _) => Forget();
        groups.Changed += (_, _) => Forget();
    }

    /// <param name="fallback">The name to use when nothing here knows it, e.g. the one a group message came with.</param>
    public string Of(DeviceId id, string? fallback = null)
    {
        string name = NameOf(id, directory.Snapshot()) ?? (string.IsNullOrWhiteSpace(fallback) ? id.ShortForm : fallback.Trim());
        List<(DeviceId Id, string? Host)> same = Index().GetValueOrDefault(name) ?? [];
        if (!same.Any(p => p.Id != id))
        {
            return name;
        }

        string? host = HostOf(id, directory.Snapshot());
        bool hostTells = !string.IsNullOrWhiteSpace(host)
            && !same.Any(p => p.Id != id && string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));
        return $"{name} · {(hostTells ? host : id.ShortForm)}";
    }

    private void Forget()
    {
        lock (gate)
        {
            byName = null;
        }
    }

    private Dictionary<string, List<(DeviceId Id, string? Host)>> Index()
    {
        lock (gate)
        {
            if (byName is { } known)
            {
                return known;
            }
        }

        IReadOnlyList<PeerInfo> live = directory.Snapshot();
        DeviceId? self = engine.IsInitialized ? engine.Identity.DeviceId : null;
        HashSet<DeviceId> ids =
        [
            .. trust.GetAll(TrustLevel.Trusted).Select(p => p.DeviceId),
            .. live.Select(p => p.DeviceId),
            .. groups.GetAll().SelectMany(g => g.Members).Select(m => m.DeviceId),
        ];
        if (self is { } me)
        {
            ids.Remove(me);
        }

        var index = new Dictionary<string, List<(DeviceId Id, string? Host)>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (DeviceId id in ids)
        {
            if (NameOf(id, live) is { } name)
            {
                if (!index.TryGetValue(name, out List<(DeviceId Id, string? Host)>? list))
                {
                    index[name] = list = [];
                }

                list.Add((id, HostOf(id, live)));
            }
        }

        lock (gate)
        {
            byName = index;
        }

        return index;
    }

    private string? NameOf(DeviceId id, IReadOnlyList<PeerInfo> live) =>
        NonEmpty(trust.Get(id)?.ShownName)
        ?? NonEmpty(live.FirstOrDefault(p => p.DeviceId == id)?.DisplayName)
        ?? NonEmpty(groups.GetAll().SelectMany(g => g.Members).FirstOrDefault(m => m.DeviceId == id)?.Name);

    private string? HostOf(DeviceId id, IReadOnlyList<PeerInfo> live) =>
        NonEmpty(trust.Get(id)?.HostName) ?? NonEmpty(live.FirstOrDefault(p => p.DeviceId == id)?.HostName);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
