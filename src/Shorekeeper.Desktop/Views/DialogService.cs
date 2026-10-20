using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
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
    public Task SendAsync(IReadOnlyList<string> paths, IReadOnlyList<(DeviceId Id, string Name)> recipients, string? groupId = null, string? text = null) =>
        SendAsync(paths, [new OfferBatch([.. recipients.Select(r => r.Id)], groupId)], text);

    /// <summary>
    /// One selection sent as one offer per batch: contacts together, fellow group members per group they share.
    /// Files another program is writing can only go as a temporary copy, so the user is asked first.
    /// </summary>
    /// <param name="text">"Gửi văn bản": sent alone when there are no files, as the offer's note with them.</param>
    public async Task SendAsync(IReadOnlyList<string> paths, IReadOnlyList<OfferBatch> batches, string? text = null)
    {
        batches = [.. batches.Where(b => b.Recipients.Count > 0)];
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if ((paths.Count == 0 && text is null) || batches.Count == 0)
        {
            return;
        }

        if (text?.Length > OfferService.MaxNoteLength)
        {
            await ChooseAsync(L.T("Không gửi được"), L.F("Văn bản dài quá {0} ký tự.", OfferService.MaxNoteLength.ToString("N0", L.Culture)), [L.T("Đóng")]);
            return;
        }

        if (paths.Count == 0)
        {
            foreach (OfferBatch batch in batches)
            {
                await offers.CreateAsync(new PreparedOffer([], []), batch.Recipients, text, snapshotLocked: false, batch.GroupId, CancellationToken.None);
            }

            OfferSent?.Invoke(this, EventArgs.Empty);
            return;
        }

        PreparedOffer prepared;
        try
        {
            prepared = await Task.Run(() => OfferService.Prepare(paths));
        }
        catch (InvalidOperationException ex)
        {
            await ChooseAsync(L.T("Không gửi được"), ex.Message, [L.T("Đóng")]);
            return;
        }

        if (prepared.Files.Count == 0)
        {
            await ChooseAsync(L.T("Không gửi được"), L.T("Không đọc được file nào trong số đã chọn."), [L.T("Đóng")]);
            return;
        }

        bool snapshotLocked = false;
        if (prepared.LockedFiles.Count > 0 && !settings.Current.AlwaysSnapshotBeforeSend)
        {
            string names = string.Join(", ", prepared.LockedFiles.Take(3).Select(f => Path.GetFileName(f.SourcePath)));
            int choice = await ChooseAsync(
                L.T("File đang được sử dụng"),
                L.F("{0} file đang được ứng dụng khác ghi ({1}). Gửi một bản sao tạm của chúng?", prepared.LockedFiles.Count, names),
                [L.T("Sao chép tạm rồi gửi"), L.T("Bỏ qua các file đó"), L.T("Hủy")]);
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
                await offers.CreateAsync(prepared, batch.Recipients, text, snapshotLocked, batch.GroupId, CancellationToken.None);
            }
        }
        catch (IOException ex)
        {
            await ChooseAsync(L.T("Không gửi được"), L.F("Không sao chép tạm được file: {0}", ex.Message), [L.T("Đóng")]);
            return;
        }

        if (prepared.Unreadable.Count > 0)
        {
            await ChooseAsync(L.T("Một số mục không đọc được"), L.F("{0} mục đã bị bỏ qua vì không đọc được (không có quyền hoặc đã bị xóa).", prepared.Unreadable.Count), [L.T("Đóng")]);
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

        IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("Chọn file để gửi"), AllowMultiple = true });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public static async Task<IReadOnlyList<string>> PickFolderAsync(Window? owner = null)
    {
        if ((owner ?? MainWindow) is not { } window)
        {
            return [];
        }

        IReadOnlyList<IStorageFolder> folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Chọn thư mục để gửi") });
        return [.. folders.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    /// <returns>The file chosen, or null when cancelled.</returns>
    public static async Task<string?> PickFileAsync(string title, FilePickerFileType type)
    {
        if (MainWindow is not { } window)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, FileTypeFilter = [type] });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <returns>Where to save, or null when cancelled.</returns>
    public static async Task<string?> PickSaveFileAsync(string title, string suggestedName, FilePickerFileType type)
    {
        if (MainWindow is not { } window)
        {
            return null;
        }

        IStorageFile? file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [type],
            DefaultExtension = type.Patterns is [var pattern, ..] ? pattern.TrimStart('*') : null,
        });
        return file?.TryGetLocalPath();
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
            ShowPopup(title, string.Join("\n\n", lines), [.. actions, L.T("Để sau")], choice =>
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
        string[] lines = viewModel.IsText ? [TransferText.Preview(viewModel.Text, 200)]
            : viewModel.Note is { } note ? [viewModel.Summary, note]
            : [viewModel.Summary];
        string[] actions = !viewModel.IsText ? [L.T("Tải"), L.T("Bỏ qua"), "Xem"]
            : viewModel.IsLink ? [L.T("Sao chép"), L.T("Mở"), "Xem"]
            : [L.T("Sao chép"), "Xem"];
        bool toasted = notifier.TryShow(viewModel.Title, lines, actions, choice => Dispatcher.UIThread.Post(() =>
        {
            if (viewModel.IsText)
            {
                if (choice == 0)
                {
                    viewModel.CopyCommand.Execute(null);
                }
                else if (choice == 1 && viewModel.IsLink)
                {
                    viewModel.OpenLinkCommand.Execute(null);
                }
                else
                {
                    viewModel.ShowCommand.Execute(null);
                }

                return;
            }

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

    /// <summary>"Mã QR" on Xóm: phones scan it to connect; its invitation ends when the window closes.</summary>
    public async Task ShowPairingQrAsync()
    {
        using var viewModel = new PairingQrViewModel(pairing);
        await ShowAsync(new PairingQrWindow { DataContext = viewModel }, viewModel);
    }

    public Task AddPeerAsync()
    {
        var viewModel = new AddPeerViewModel(finder, peer => ConnectAsync(peer.DeviceId, peer.Name));
        return ShowAsync(new AddPeerWindow { DataContext = viewModel }, viewModel);
    }

    /// <inheritdoc cref="PasswordPromptViewModel"/>
    public static async Task<string?> PromptPasswordAsync(string title, string label, string okText, bool confirm, Func<string, Task<string?>> check)
    {
        var viewModel = new PasswordPromptViewModel(title, label, okText, confirm, check);
        await ShowAsync(new PasswordPromptWindow { DataContext = viewModel }, viewModel);
        return viewModel.Result;
    }

    /// <param name="multiline">A larger box where Enter starts a new line, e.g. for "Gửi văn bản".</param>
    public static async Task<string?> PromptAsync(string title, string label, string initialText, string? okText = null, bool multiline = false)
    {
        var viewModel = new TextPromptViewModel(title, label, initialText, okText) { IsMultiline = multiline };
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

    /// <summary>"Tự nhận file…" for one contact; <paramref name="save"/> gets the choice when the user saves.</summary>
    public static async Task ChooseAutoAcceptAsync(string name, long? current, double generalLimitGb, Func<long?, Task> save)
    {
        var window = new AutoAcceptWindow();
        window.DataContext = new AutoAcceptChoiceViewModel(name, current, generalLimitGb, save, window.Close);
        if (MainWindow is { } owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>Puts text on the clipboard, through the main window (it exists while hidden in the tray).</summary>
    public static async Task CopyTextAsync(string text)
    {
        if (MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(DataFormat.Text, text);
        }
    }

    /// <summary>Opens <paramref name="text"/> in the browser when it is a web address; anything else is left alone.</summary>
    public static void OpenLink(string text)
    {
        if (TransferText.LinkOf(text) is { } link)
        {
            try
            {
                Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            catch (Win32Exception)
            {
                // No browser registered: nothing better to do than leave it on the card.
            }
        }
    }

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
