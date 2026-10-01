using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
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

    public string Title { get; } = $"Kết nối với {name}";

    [ObservableProperty]
    public partial string Message { get; set; } = "Đang gửi yêu cầu…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    public partial string? Code { get; set; }

    [ObservableProperty]
    public partial bool IsWaiting { get; set; } = true;

    [ObservableProperty]
    public partial string CloseText { get; set; } = "Hủy";

    public bool HasCode => Code is not null;

    public async Task RunAsync()
    {
        try
        {
            PairingOutcome outcome = await pairing.RequestAsync(target, null, pending =>
            {
                Code = pending.Code;
                Message = $"Đang chờ {pending.Name} chấp nhận…";
            }, cancellation.Token);

            Message = outcome switch
            {
                PairingOutcome.Accepted => $"Đã kết nối với {name}.",
                PairingOutcome.Declined => $"{name} đã từ chối.",
                PairingOutcome.Expired => "Yêu cầu đã hết hạn.",
                _ => "Đã hủy.",
            };
        }
        catch (PeerUnreachableException)
        {
            Message = $"Không kết nối được tới {name}. Máy đó có thể đã tắt Shorekeeper hoặc firewall đang chặn.";
        }
        catch (PeerApiException ex) when (ex.Code == ApiErrorCodes.RateLimited)
        {
            Message = "Bạn đã gửi nhiều yêu cầu cho máy này. Hãy thử lại sau ít phút.";
        }
        catch (PeerApiException)
        {
            Message = $"{name} không nhận yêu cầu kết nối.";
        }
        finally
        {
            IsWaiting = false;
            CloseText = "Đóng";
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

    public string Title => $"{request.Name} ({request.Host}) muốn kết nối";

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
        Status = "Đang tìm…";
        FindResult result = await finder.FindAsync(Input, cancellationToken);
        Found = result.Peer;
        Status = result.Peer is { } peer ? $"Tìm thấy: {peer.Name} · {peer.Host} ({peer.Endpoint.Address})" : result.Error;
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

/// <summary>Single-line text input, e.g. "Đặt tên gợi nhớ".</summary>
public sealed partial class TextPromptViewModel(string title, string label, string initialText) : DialogViewModel
{
    public string Title { get; } = title;

    public string Label { get; } = label;

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
