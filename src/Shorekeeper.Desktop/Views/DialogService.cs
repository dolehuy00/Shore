using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Platform;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.Views;

/// <summary>Opens the app's small windows. Must be called on the UI thread.</summary>
public sealed class DialogService(
    PairingService pairing,
    ManualPeerFinder finder,
    OfferService offers,
    InboxService inbox,
    SettingsService settings,
    INotifier notifier)
{
    private static readonly TimeSpan OfferPopupLifetime = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, Window> incoming = [];

    /// <summary>An offer was created; the main window shows the "Đã gửi" page.</summary>
    public event EventHandler? OfferSent;

    /// <summary>The user pressed "Xem" on a popup or clicked a toast: the main window shows that page.</summary>
    public event EventHandler<NavPage>? PageRequested;

    /// <summary>Sends files/folders to contacts, or to members of <paramref name="groupId"/> (docs/06-file-transfer.md §6).</summary>
    public Task SendAsync(IReadOnlyList<string> paths, IReadOnlyList<(DeviceId Id, string Name)> recipients, string? groupId = null) =>
        SendAsync(paths, [new OfferBatch([.. recipients.Select(r => r.Id)], groupId)]);

    /// <summary>
    /// One selection sent as one offer per batch: contacts together, fellow group members per group they share.
    /// Files another program is writing can only go as a temporary copy, so the user is asked first.
    /// </summary>
    public async Task SendAsync(IReadOnlyList<string> paths, IReadOnlyList<OfferBatch> batches)
    {
        batches = [.. batches.Where(b => b.Recipients.Count > 0)];
        if (paths.Count == 0 || batches.Count == 0)
        {
            return;
        }

        PreparedOffer prepared;
        try
        {
            prepared = await Task.Run(() => OfferService.Prepare(paths));
        }
        catch (InvalidOperationException ex)
        {
            await ChooseAsync("Không gửi được", ex.Message, ["Đóng"]);
            return;
        }

        if (prepared.Files.Count == 0)
        {
            await ChooseAsync("Không gửi được", "Không đọc được file nào trong số đã chọn.", ["Đóng"]);
            return;
        }

        bool snapshotLocked = false;
        if (prepared.LockedFiles.Count > 0 && !settings.Current.AlwaysSnapshotBeforeSend)
        {
            string names = string.Join(", ", prepared.LockedFiles.Take(3).Select(f => Path.GetFileName(f.SourcePath)));
            int choice = await ChooseAsync(
                "File đang được sử dụng",
                $"{prepared.LockedFiles.Count} file đang được ứng dụng khác ghi ({names}). Gửi một bản sao tạm của chúng?",
                ["Sao chép tạm rồi gửi", "Bỏ qua các file đó", "Hủy"]);
            if (choice is < 0 or 2)
            {
                return;
            }

            snapshotLocked = choice == 0;
        }

        try
        {
            foreach (OfferBatch batch in batches)
            {
                await offers.CreateAsync(prepared, batch.Recipients, null, snapshotLocked, batch.GroupId, CancellationToken.None);
            }
        }
        catch (IOException ex)
        {
            await ChooseAsync("Không gửi được", $"Không sao chép tạm được file: {ex.Message}", ["Đóng"]);
            return;
        }

        if (prepared.Unreadable.Count > 0)
        {
            await ChooseAsync("Một số mục không đọc được", $"{prepared.Unreadable.Count} mục đã bị bỏ qua vì không đọc được (không có quyền hoặc đã bị xóa).", ["Đóng"]);
        }

        OfferSent?.Invoke(this, EventArgs.Empty);
    }

    /// <param name="owner">The window asking; the main window when null.</param>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(Window? owner = null)
    {
        if ((owner ?? MainWindow) is not { } window)
        {
            return [];
        }

        IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Chọn file để gửi", AllowMultiple = true });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public static async Task<IReadOnlyList<string>> PickFolderAsync(Window? owner = null)
    {
        if ((owner ?? MainWindow) is not { } window)
        {
            return [];
        }

        IReadOnlyList<IStorageFolder> folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Chọn thư mục để gửi" });
        return [.. folders.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    /// <returns>The file ids picked, or null when cancelled.</returns>
    public static async Task<IReadOnlyList<string>?> PickOfferFilesAsync(ReceivedOffer offer)
    {
        var viewModel = new PickOfferFilesViewModel(offer);
        await ShowAsync(new PickOfferFilesWindow { DataContext = viewModel }, viewModel);
        return viewModel.Selected;
    }

    /// <returns>The people ticked, or null when cancelled.</returns>
    public static async Task<IReadOnlyList<PickablePeer>?> PickPeopleAsync(string title, string confirmText, string emptyText, IReadOnlyList<PickablePeer> people)
    {
        var viewModel = new PickPeopleViewModel(title, confirmText, emptyText, people);
        await ShowAsync(new PickPeopleWindow { DataContext = viewModel }, viewModel);
        return viewModel.Selected;
    }

    /// <summary>
    /// A question that blocks nothing, asked even when the app sits in the tray (join requests, invitations):
    /// a Windows toast, or a popup with an extra "Để sau" when toasts are off. <paramref name="onAction"/> gets the
    /// index in <paramref name="actions"/>, on the UI thread; clicking the toast itself opens <paramref name="page"/>.
    /// </summary>
    public void Ask(string title, IReadOnlyList<string> lines, IReadOnlyList<string> actions, NavPage page, Action<int> onAction)
    {
        bool toasted = notifier.TryShow(title, lines, actions, choice => Dispatcher.UIThread.Post(() =>
        {
            if (choice < 0)
            {
                PageRequested?.Invoke(this, page);
            }
            else
            {
                onAction(choice);
            }
        }));
        if (!toasted)
        {
            ShowPopup(title, string.Join("\n\n", lines), [.. actions, "Để sau"], choice =>
            {
                if (choice >= 0 && choice < actions.Count)
                {
                    onAction(choice);
                }
            });
        }
    }

    /// <summary>
    /// A question that does not block the main window, shown even when the app sits in the tray.
    /// <paramref name="onChoice"/> gets the button index, or -1 when closed.
    /// </summary>
    private static void ShowPopup(string title, string message, IReadOnlyList<string> choices, Action<int> onChoice)
    {
        var viewModel = new ChoiceViewModel(title, message, choices);
        var window = new ChoiceWindow { DataContext = viewModel, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Closed += (_, _) => onChoice(viewModel.Choice);
        window.Show();
    }

    /// <returns>Index of the button pressed, or -1 if the window was closed.</returns>
    public static async Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices)
    {
        var viewModel = new ChoiceViewModel(title, message, choices);
        await ShowAsync(new ChoiceWindow { DataContext = viewModel }, viewModel);
        return viewModel.Choice;
    }

    /// <summary>
    /// "Huy gửi cho bạn …" [Tải] [Bỏ qua] [Xem] (docs/10-ux.md §4): a Windows toast, or a small popup in the corner
    /// of the screen when toasts are off.
    /// </summary>
    public void ShowIncomingOffer(ReceivedOffer offer, string senderName)
    {
        var key = new OfferKey(offer.SenderId, offer.OfferId);
        var viewModel = new IncomingOfferViewModel(offer, senderName, () => inbox.Download(key), () => inbox.DeclineAsync(key), () => PageRequested?.Invoke(this, NavPage.Inbox));
        string[] lines = viewModel.Note is { } note ? [viewModel.Summary, note] : [viewModel.Summary];
        bool toasted = notifier.TryShow(viewModel.Title, lines, ["Tải", "Bỏ qua", "Xem"], choice => Dispatcher.UIThread.Post(() =>
        {
            switch (choice)
            {
                case 0:
                    viewModel.DownloadCommand.Execute(null);
                    break;
                case 1:
                    viewModel.DeclineCommand.Execute(null);
                    break;
                default:
                    viewModel.ShowCommand.Execute(null);
                    break;
            }
        }));
        if (toasted)
        {
            return;
        }

        var window = new IncomingOfferWindow { DataContext = viewModel, Topmost = true, ShowActivated = false };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Opened += (_, _) =>
        {
            // Bottom-right corner of the primary screen, like a notification.
            if (window.Screens.Primary is { } screen)
            {
                PixelRect area = screen.WorkingArea;
                var size = PixelSize.FromSize(window.Bounds.Size, screen.Scaling);
                window.Position = new PixelPoint(area.Right - size.Width - 16, area.Bottom - size.Height - 16);
            }
        };

        window.Show();
        DispatcherTimer.RunOnce(window.Close, OfferPopupLifetime);
    }

    public async Task ConnectAsync(DeviceId target, string name)
    {
        using var viewModel = new OutgoingPairingViewModel(pairing, target, name);
        var window = new OutgoingPairingWindow { DataContext = viewModel };
        Task running = Task.CompletedTask;
        window.Closing += (_, _) => viewModel.Abort();
        window.Opened += (_, _) => running = viewModel.RunAsync();
        await ShowAsync(window, viewModel);
        // Let the request finish cancelling before its token source is disposed.
        await running;
    }

    public Task AddPeerAsync()
    {
        var viewModel = new AddPeerViewModel(finder, peer => ConnectAsync(peer.DeviceId, peer.Name));
        return ShowAsync(new AddPeerWindow { DataContext = viewModel }, viewModel);
    }

    public static async Task<string?> PromptAsync(string title, string label, string initialText)
    {
        var viewModel = new TextPromptViewModel(title, label, initialText);
        await ShowAsync(new TextPromptWindow { DataContext = viewModel }, viewModel);
        return viewModel.Confirmed ? viewModel.Text : null;
    }

    /// <summary>Shown even when the app sits in the tray: the requester is waiting for an answer.</summary>
    public void ShowIncoming(IncomingPairing request)
    {
        var viewModel = new IncomingPairingViewModel(pairing, request);
        var window = new IncomingPairingWindow { DataContext = viewModel, Topmost = true };
        bool decided = false;
        viewModel.CloseRequested += (_, _) =>
        {
            decided = true;
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            incoming.Remove(request.Id);
            if (!decided)
            {
                // Closing the window without choosing counts as declining.
                pairing.Decline(request.Id);
            }
        };

        incoming[request.Id] = window;
        window.Show();
        window.Activate();
    }

    /// <summary>The requester cancelled or the request expired.</summary>
    public void CloseIncoming(string requestId)
    {
        if (incoming.Remove(requestId, out Window? window))
        {
            window.Close();
        }
    }

    private static Window? MainWindow => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
        .Windows.FirstOrDefault(w => w is MainWindow { IsVisible: true });

    private static async Task ShowAsync(Window window, DialogViewModel viewModel)
    {
        viewModel.CloseRequested += (_, _) => window.Close();
        Window? owner = MainWindow;

        if (owner is not null)
        {
            await window.ShowDialog(owner);
            return;
        }

        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await closed.Task;
    }
}

/// <summary>Recipients of one offer; <see cref="GroupId"/> is set when they get it as fellow members of that group.</summary>
public sealed record OfferBatch(IReadOnlyList<DeviceId> Recipients, string? GroupId);
