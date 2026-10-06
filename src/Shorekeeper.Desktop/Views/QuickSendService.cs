using Avalonia.Threading;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;
using Shorekeeper.Core.Trust;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.Views;

/// <summary>
/// The "Gửi nhanh" window (docs/10-ux.md §10–11), opened by the global shortcut, the tray menu and
/// Explorer's "Gửi bằng Shorekeeper…". Must be called on the UI thread.
/// </summary>
public sealed class QuickSendService(
    DialogService dialogs,
    TrustStore trust,
    GroupStore groups,
    OfferService offers,
    PeerNames names,
    LocalDevice device,
    SettingsService settings,
    IGlobalHotkey hotkey)
{
    private QuickSendWindow? window;

    /// <summary>The shortcut is on in Settings but another program already uses it.</summary>
    public bool IsHotkeyTaken { get; private set; }

    public string HotkeyText => hotkey.DisplayText;

    public event EventHandler? HotkeyChanged;

    /// <summary>Registers the shortcut (when turned on) and follows the setting.</summary>
    public void Start()
    {
        hotkey.Pressed += (_, _) => Dispatcher.UIThread.Post(() => Open([]));
        settings.Changed += (_, current) => Dispatcher.UIThread.Post(() => ApplyHotkey(current));
        ApplyHotkey(settings.Current);
    }

    /// <summary>Opens the window, or adds <paramref name="paths"/> to the one already open (Explorer starts us once per file).</summary>
    public void Open(IReadOnlyList<string> paths)
    {
        if (window is not null)
        {
            (window.DataContext as QuickSendViewModel)?.Add(paths);
            window.Activate();
            return;
        }

        var viewModel = new QuickSendViewModel(People(), paths);
        var opened = new QuickSendWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => opened.Close();
        opened.Closed += (_, _) =>
        {
            window = null;
            // Sent once the window is gone, so questions about locked files are not hidden behind it.
            if (viewModel.Result is { } result)
            {
                _ = dialogs.SendAsync(result.Paths, result.Batches);
            }
        };

        window = opened;
        opened.Show();
        opened.Activate();
    }

    private void ApplyHotkey(EffectiveSettings current)
    {
        bool taken = !hotkey.SetEnabled(current.QuickSendHotkey) && current.QuickSendHotkey;
        if (taken != IsHotkeyTaken)
        {
            IsHotkeyTaken = taken;
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Contacts, then fellow group members; people sent to lately first, then those online.</summary>
    private List<QuickSendPerson> People()
    {
        Dictionary<DeviceId, DateTimeOffset> lastSent = [];
        foreach (SentOffer offer in offers.GetOffers())
        {
            foreach (SentRecipient recipient in offer.Recipients)
            {
                if (lastSent.GetValueOrDefault(recipient.DeviceId) < offer.CreatedAt)
                {
                    lastSent[recipient.DeviceId] = offer.CreatedAt;
                }
            }
        }

        List<QuickSendPerson> people =
        [
            .. trust.GetAll(TrustLevel.Trusted)
                .Select(record => new QuickSendPerson(record.DeviceId, names.Of(record.DeviceId), Online(record.DeviceId), null)),
        ];
        if (settings.Current.GroupsEnabled)
        {
            foreach (Group group in groups.GetAll().Where(g => g.Role != GroupRole.Pending))
            {
                foreach (GroupMember member in group.Members)
                {
                    if (member.DeviceId != device.DeviceId
                        && trust.GetLevel(member.DeviceId) == TrustLevel.Unknown
                        && people.TrueForAll(p => p.DeviceId != member.DeviceId))
                    {
                        string name = names.Of(member.DeviceId);
                        people.Add(new QuickSendPerson(
                            member.DeviceId,
                            name == member.DeviceId.ShortForm ? member.Name : name,
                            $"{Online(member.DeviceId)} · nhóm {group.Name}",
                            group.Id));
                    }
                }
            }
        }

        return
        [
            .. people
                .OrderByDescending(p => lastSent.GetValueOrDefault(p.DeviceId))
                .ThenByDescending(p => names.IsOnline(p.DeviceId))
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    private string Online(DeviceId id) => names.IsOnline(id) ? "online" : "offline";
}
