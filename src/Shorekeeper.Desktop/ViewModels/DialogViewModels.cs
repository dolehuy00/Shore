using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>A view model shown in its own window; the window closes when <see cref="CloseRequested"/> fires.</summary>
public abstract class DialogViewModel : ObservableObject
{
    public event EventHandler? CloseRequested;

    protected void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>"Đang chờ X chấp nhận… Mã: 482 913" (docs/10-ux.md §5).</summary>
public sealed partial class OutgoingPairingViewModel(PairingService pairing, DeviceId target, string name) : DialogViewModel, IDisposable
{
    private readonly CancellationTokenSource cancellation = new();

    public string Title { get; } = L.F("Kết nối với {0}", name);

    [ObservableProperty]
    public partial string Message { get; set; } = L.T("Đang gửi yêu cầu…");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    public partial string? Code { get; set; }

    [ObservableProperty]
    public partial bool IsWaiting { get; set; } = true;

    [ObservableProperty]
    public partial string CloseText { get; set; } = L.T("Hủy");

    public bool HasCode => Code is not null;

    public async Task RunAsync()
    {
        try
        {
            PairingOutcome outcome = await pairing.RequestAsync(target, null, pending =>
            {
                Code = pending.Code;
                Message = L.F("Đang chờ {0} chấp nhận…", pending.Name);
            }, cancellation.Token);

            Message = outcome switch
            {
                PairingOutcome.Accepted => L.F("Đã kết nối với {0}.", name),
                PairingOutcome.Declined => L.F("{0} đã từ chối.", name),
                PairingOutcome.Expired => L.T("Yêu cầu đã hết hạn."),
                _ => L.T("Đã hủy."),
            };
        }
        catch (PeerUnreachableException)
        {
            Message = L.F("Không kết nối được tới {0}. Máy đó có thể đã tắt Shorekeeper hoặc firewall đang chặn.", name);
        }
        catch (PeerApiException ex) when (ex.Code == ApiErrorCodes.RateLimited)
        {
            Message = L.T("Bạn đã gửi nhiều yêu cầu cho máy này. Hãy thử lại sau ít phút.");
        }
        catch (PeerApiException)
        {
            Message = L.F("{0} không nhận yêu cầu kết nối.", name);
        }
        finally
        {
            IsWaiting = false;
            CloseText = L.T("Đóng");
        }
    }

    [RelayCommand]
    private void Close()
    {
        Abort();
        RequestClose();
    }

    /// <summary>Stops waiting (the peer is told the request is cancelled). Also used when the window is closed directly.</summary>
    public void Abort() => cancellation.Cancel();

    public void Dispose() => cancellation.Dispose();
}

/// <summary>"Huy (PC-DEV-03) muốn kết nối · Mã: 482 913" [Chấp nhận] [Từ chối] [Chặn].</summary>
public sealed partial class IncomingPairingViewModel(PairingService pairing, IncomingPairing request) : DialogViewModel
{
    public string RequestId => request.Id;

    public string Title => L.F("{0} ({1}) muốn kết nối", request.Name, request.Host);

    public string Code => request.Code;

    public string? Note => request.Note is null ? null : $"\"{request.Note}\"";

    public bool HasNote => request.Note is not null;

    [RelayCommand]
    private void Accept()
    {
        pairing.Accept(request.Id);
        RequestClose();
    }

    [RelayCommand]
    private void Decline()
    {
        pairing.Decline(request.Id);
        RequestClose();
    }

    [RelayCommand]
    private async Task BlockAsync()
    {
        await pairing.BlockAsync(request.Id);
        RequestClose();
    }
}

/// <summary>"Thêm máy ở mạng khác" (docs/10-ux.md §7).</summary>
public sealed partial class AddPeerViewModel(ManualPeerFinder finder, Func<FoundPeer, Task> connect) : DialogViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    public partial string Input { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFound))]
    public partial FoundPeer? Found { get; set; }

    public bool HasFound => Found is not null;

    private bool CanFind => !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync(CancellationToken cancellationToken)
    {
        Found = null;
        Status = L.T("Đang tìm…");
        FindResult result = await finder.FindAsync(Input, cancellationToken);
        Found = result.Peer;
        Status = result.Peer is { } peer ? L.F("Tìm thấy: {0} · {1} ({2})", peer.Name, peer.Host, peer.Endpoint.Address) : result.Error;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (Found is not { } peer)
        {
            return;
        }

        await finder.SaveAsync(Input);
        RequestClose();
        await connect(peer);
    }

    [RelayCommand]
    private async Task SaveOnlyAsync()
    {
        await finder.SaveAsync(Input);
        RequestClose();
    }

    [RelayCommand]
    private void Cancel() => RequestClose();
}

/// <summary>A message with one to three buttons; <see cref="Choice"/> is the index pressed, or -1 if closed.</summary>
public sealed partial class ChoiceViewModel(string title, string message, IReadOnlyList<string> choices) : DialogViewModel
{
    public string Title { get; } = title;

    public string Message { get; } = message;

    public IReadOnlyList<ChoiceButton> Buttons { get; } = [.. choices.Select((text, index) => new ChoiceButton(text, index))];

    public int Choice { get; private set; } = -1;

    [RelayCommand]
    private void Choose(ChoiceButton button)
    {
        Choice = button.Index;
        RequestClose();
    }
}

public sealed record ChoiceButton(string Text, int Index);

public sealed partial class PickablePeer(DeviceId deviceId, string name, string detail, bool isChecked) : ObservableObject
{
    public DeviceId DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public string Detail { get; } = detail;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;
}

/// <summary>Ticks people: recipients when sending to a group, people to invite into one (docs/10-ux.md §6).</summary>
public sealed partial class PickPeopleViewModel : DialogViewModel
{
    public PickPeopleViewModel(string title, string confirmText, string emptyText, IReadOnlyList<PickablePeer> people)
    {
        Title = title;
        ConfirmText = confirmText;
        EmptyText = emptyText;
        People = people;
        foreach (PickablePeer person in People)
        {
            person.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SummaryText));
        }
    }

    public string Title { get; }

    public string ConfirmText { get; }

    public string EmptyText { get; }

    public IReadOnlyList<PickablePeer> People { get; }

    public bool IsEmpty => People.Count == 0;

    public string SummaryText => L.F("Đã chọn {0}/{1} người", People.Count(p => p.IsChecked), People.Count);

    /// <summary>Set when the user confirms with at least one person.</summary>
    public IReadOnlyList<PickablePeer>? Selected { get; private set; }

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    [RelayCommand]
    private void Confirm()
    {
        PickablePeer[] picked = [.. People.Where(p => p.IsChecked)];
        if (picked.Length > 0)
        {
            Selected = picked;
        }

        RequestClose();
    }

    [RelayCommand]
    private void Cancel() => RequestClose();

    private void SetAll(bool value)
    {
        foreach (PickablePeer person in People)
        {
            person.IsChecked = value;
        }
    }
}

public sealed partial class PickableFile(string fileId, string path, long size) : ObservableObject
{
    public string FileId { get; } = fileId;

    public string Path { get; } = path;

    public long Size { get; } = size;

    public string SizeText { get; } = TransferText.Size(size);

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;
}

/// <summary>"Chọn file…": download only some files of an offer (docs/06-file-transfer.md §3.2).</summary>
public sealed partial class PickOfferFilesViewModel : DialogViewModel
{
    public PickOfferFilesViewModel(ReceivedOffer offer)
    {
        Files = [.. offer.Files
            .Where(f => !f.IsDirectory && f.State != InboxFileState.Completed)
            .Select(f => new PickableFile(f.FileId, f.RelativePath, f.Size))];
        foreach (PickableFile file in Files)
        {
            file.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SummaryText));
        }
    }

    public IReadOnlyList<PickableFile> Files { get; }

    public string SummaryText =>
        $"{Files.Count(f => f.IsChecked)}/{Files.Count} file · {TransferText.Size(Files.Where(f => f.IsChecked).Sum(f => f.Size))}";

    /// <summary>Set when the user confirms with at least one file.</summary>
    public IReadOnlyList<string>? Selected { get; private set; }

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    [RelayCommand]
    private void Confirm()
    {
        string[] picked = [.. Files.Where(f => f.IsChecked).Select(f => f.FileId)];
        if (picked.Length > 0)
        {
            Selected = picked;
        }

        RequestClose();
    }

    [RelayCommand]
    private void Cancel() => RequestClose();

    private void SetAll(bool value)
    {
        foreach (PickableFile file in Files)
        {
            file.IsChecked = value;
        }
    }
}

/// <summary>"Huy gửi cho bạn · app.zip + 1 mục · 2.4 GB" [Tải] [Bỏ qua] [Xem] (docs/10-ux.md §4).</summary>
public sealed partial class IncomingOfferViewModel(ReceivedOffer offer, string senderName, Action download, Func<Task> decline, Action show)
    : DialogViewModel
{
    public string Title { get; } = L.F("{0} gửi cho bạn", senderName);

    public string Summary { get; } = $"{TransferText.Summary(offer.Files.Select(f => f.RelativePath))} · {TransferText.Size(offer.TotalBytes)}";

    public string? Note { get; } = offer.Note is null ? null : $"\"{offer.Note}\"";

    public bool HasNote => offer.Note is not null;

    [RelayCommand]
    private void Download()
    {
        download();
        RequestClose();
    }

    [RelayCommand]
    private async Task DeclineAsync()
    {
        await decline();
        RequestClose();
    }

    [RelayCommand]
    private void Show()
    {
        show();
        RequestClose();
    }
}

/// <summary>Single-line text input, e.g. "Đặt tên gợi nhớ".</summary>
/// <param name="isPassword">Typed characters are hidden.</param>
public sealed partial class TextPromptViewModel(string title, string label, string initialText, bool isPassword = false, string? okText = null) : DialogViewModel
{
    public string Title { get; } = title;

    public string Label { get; } = label;

    public char PasswordChar { get; } = isPassword ? '•' : '\0';

    public string OkText { get; } = okText ?? L.T("Lưu");

    [ObservableProperty]
    public partial string Text { get; set; } = initialText;

    public bool Confirmed { get; private set; }

    [RelayCommand]
    private void Ok()
    {
        Confirmed = true;
        RequestClose();
    }

    [RelayCommand]
    private void Cancel() => RequestClose();
}
