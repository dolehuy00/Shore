using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Shorekeeper.Core.Identity;
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
    SettingsService settings)
{
    private static readonly TimeSpan OfferPopupLifetime = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, Window> incoming = [];

    /// <summary>An offer was created; the main window shows the "Đã gửi" page.</summary>
    public event EventHandler? OfferSent;

    /// <summary>The user pressed "Xem" on the incoming-offer popup.</summary>
    public event EventHandler? InboxRequested;

    /// <summary>
    /// Sends files/folders to contacts (docs/06-file-transfer.md §6). Files another program is writing
    /// can only go as a temporary copy, so the user is asked first.
    /// </summary>
    public async Task SendAsync(IReadOnlyList<string> paths, IReadOnlyList<(DeviceId Id, string Name)> recipients)
    {
        if (paths.Count == 0 || recipients.Count == 0)
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
            await offers.CreateAsync(prepared, [.. recipients.Select(r => r.Id)], null, snapshotLocked, CancellationToken.None);
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

    public static async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        if (MainWindow is not { } window)
        {
            return [];
        }

        IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Chọn file để gửi", AllowMultiple = true });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public static async Task<IReadOnlyList<string>> PickFolderAsync()
    {
        if (MainWindow is not { } window)
        {
            return [];
        }

        IReadOnlyList<IStorageFolder> folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Chọn thư mục để gửi" });
        return [.. folders.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    /// <returns>Index of the button pressed, or -1 if the window was closed.</returns>
    public static async Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices)
    {
        var viewModel = new ChoiceViewModel(title, message, choices);
        await ShowAsync(new ChoiceWindow { DataContext = viewModel }, viewModel);
        return viewModel.Choice;
    }

    /// <summary>Small popup in the corner of the screen: "Huy gửi cho bạn …" [Tải] [Bỏ qua] [Xem].</summary>
    public void ShowIncomingOffer(ReceivedOffer offer, string senderName)
    {
        var key = new OfferKey(offer.SenderId, offer.OfferId);
        var viewModel = new IncomingOfferViewModel(offer, senderName, () => inbox.Download(key), () => inbox.DeclineAsync(key), () => InboxRequested?.Invoke(this, EventArgs.Empty));
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
