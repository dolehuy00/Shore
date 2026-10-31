using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Settings;
using Shorekeeper.Core.Trust;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;
using Shorekeeper.UI.ViewModels;

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
    AppPaths appPaths,
    IGlobalHotkey hotkey)
{
    private QuickSendWindow? window;
    private bool paused;

    /// <summary>The shortcut is on in Settings but another program already uses it.</summary>
    public bool IsHotkeyTaken { get; private set; }

    public event EventHandler? HotkeyChanged;

    /// <summary>
    /// Lets go of the shortcut while Settings records a new one, so pressing the current one there is seen
    /// by the window instead of opening "Gửi nhanh".
    /// </summary>
    public void PauseHotkey(bool pause)
    {
        paused = pause;
        ApplyHotkey(settings.Current);
    }

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
                _ = dialogs.SendAsync(result.Paths, result.Batches, result.Text);
            }
        };

        window = opened;
        opened.Show();
        opened.Activate();
        if (paths.Count == 0)
        {
            _ = FillFromClipboardAsync(opened, viewModel, appPaths.ClipboardDirectory);
        }
    }

    /// <summary>
    /// Opened by the shortcut or the tray while a link or some text is copied (and no files): it is filled in, so copy,
    /// shortcut, pick the phone, Enter. A picture copied as a picture (Win+Shift+S) is added as a PNG instead. Either
    /// shows in the window before anything is sent.
    /// </summary>
    private static async Task FillFromClipboardAsync(TopLevel window, QuickSendViewModel viewModel, string pictures)
    {
        if (window.Clipboard is not { } clipboard || (await clipboard.TryGetFilesAsync())?.Length > 0)
        {
            return;
        }

        if (await clipboard.TryGetTextAsync() is { Length: <= OfferService.MaxNoteLength } text && !string.IsNullOrWhiteSpace(text))
        {
            if (viewModel.Text.Length == 0)
            {
                viewModel.Text = text;
            }
        }
        else if (await ClipboardImage.SaveAsync(clipboard, pictures) is { } picture)
        {
            viewModel.Add([picture]);
        }
    }

    private void ApplyHotkey(EffectiveSettings current)
    {
        if (paused)
        {
            hotkey.Register(null);
            return;
        }

        bool taken = !hotkey.Register(current.QuickSendHotkey ? current.QuickSendShortcut : null) && current.QuickSendHotkey;
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
            .. trust.GetAll(TrustLevel.Trusted).Select(record => Person(record.DeviceId, names.Of(record.DeviceId), record.HostName, null, lastSent)),
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
                        people.Add(Person(member.DeviceId, name == member.DeviceId.ShortForm ? member.Name : name, "", group, lastSent));
                    }
                }
            }
        }

        return
        [
            .. people
                .OrderByDescending(p => lastSent.GetValueOrDefault(p.DeviceId))
                .ThenByDescending(p => !p.IsInactive)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <param name="hostName">The computer's name as last known, used while it is offline.</param>
    private QuickSendPerson Person(DeviceId id, string name, string hostName, Group? group, Dictionary<DeviceId, DateTimeOffset> lastSent)
    {
        PeerInfo? live = names.Live(id);
        List<string> detail = [];
        if ((live?.HostName ?? hostName) is { Length: > 0 } host)
        {
            detail.Add(host);
        }

        if (lastSent.TryGetValue(id, out DateTimeOffset sentAt))
        {
            detail.Add(L.F("gửi lần cuối {0}", TransferText.Ago(sentAt, DateTimeOffset.UtcNow)));
        }

        return new QuickSendPerson(id, name, group?.Id)
        {
            IsPhone = names.IsPhone(id),
            IsBusy = live?.IsBusy == true,
            IsInactive = live is null || live.State == PeerState.Stale,
            StatusText = live is null ? "Offline" : live.IsBusy ? L.T("Bận") : "Online",
            Detail = string.Join(" · ", detail),
            GroupText = group is null ? "" : L.F("Nhóm {0}", group.Name),
        };
    }
}
