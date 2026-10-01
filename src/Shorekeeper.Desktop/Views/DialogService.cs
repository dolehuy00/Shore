using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Shorekeeper.Core.Identity;
using Shorekeeper.Desktop.ViewModels;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.Views;

/// <summary>Opens the app's small windows. Must be called on the UI thread.</summary>
public sealed class DialogService(PairingService pairing, ManualPeerFinder finder)
{
    private readonly Dictionary<string, Window> incoming = [];

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

    private static async Task ShowAsync(Window window, DialogViewModel viewModel)
    {
        viewModel.CloseRequested += (_, _) => window.Close();
        Window? owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
            .Windows.FirstOrDefault(w => w is MainWindow { IsVisible: true });

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
