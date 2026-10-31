using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Android.ViewModels;

public sealed partial class IncomingPairingViewModel(IncomingPairing request, MainViewModel owner)
{
    public string Id { get; } = request.Id;

    public string Title { get; } = L.F("{0} muốn kết nối", request.Name);

    public string Host { get; } = request.Host;

    public string Code { get; } = MainViewModel.Format(request.Code);

    [RelayCommand]
    private void Accept() => owner.Accept(this);

    [RelayCommand]
    private void Decline() => owner.Decline(this);
}
