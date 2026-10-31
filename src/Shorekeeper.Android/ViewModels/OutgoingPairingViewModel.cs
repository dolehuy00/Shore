using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;

namespace Shorekeeper.Android.ViewModels;

public sealed partial class OutgoingPairingViewModel(string name, MainViewModel owner) : ObservableObject
{
    public CancellationTokenSource Cancellation { get; } = new();

    public string Title { get; } = L.F("Kết nối với {0}", name);

    [ObservableProperty]
    public partial string Code { get; set; } = "…";

    [ObservableProperty]
    public partial string Status { get; set; } = L.T("Đang gửi yêu cầu… So mã này với mã trên máy kia.");

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    public void Finish(string status)
    {
        Status = status;
        IsFinished = true;
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsFinished)
        {
            Cancellation.Cancel();
        }

        owner.CloseOutgoing();
    }
}
