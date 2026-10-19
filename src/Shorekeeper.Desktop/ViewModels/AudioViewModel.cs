using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Shorekeeper.Core.Localization;
using Shorekeeper.Desktop.Views;
using Shorekeeper.Engine.Audio;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>The "Âm thanh" page: this computer's sound for connected devices that choose to listen (docs/10-ux.md §12).</summary>
public sealed partial class AudioViewModel : ObservableObject
{
    private readonly AudioService audio;
    private readonly PeerNames names;
    private bool refreshing;

    public AudioViewModel(AudioService audio, PeerNames names, DialogService dialogs)
    {
        this.audio = audio;
        this.names = names;
        audio.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        audio.Notice += (_, message) => Dispatcher.UIThread.Post(() => dialogs.Ask(L.T("Phát âm thanh"), [message], [], NavPage.Audio, _ => { }));
        Refresh();
    }

    public bool CanPlay => audio.CanPlay;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>Who listens right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasListeners))]
    public partial IReadOnlyList<string> ListenerNames { get; set; } = [];

    public bool HasListeners => ListenerNames.Count > 0;

    partial void OnIsPlayingChanged(bool value)
    {
        if (!refreshing)
        {
            audio.SetPlaying(value);
        }
    }

    private void Refresh()
    {
        refreshing = true;
        IsPlaying = audio.IsPlaying;
        refreshing = false;
        ListenerNames = [.. audio.Listeners.Select(l => names.Of(l.PeerId))];
    }
}
