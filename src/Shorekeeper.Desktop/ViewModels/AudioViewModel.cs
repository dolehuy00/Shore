using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Audio;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>A device listening to ours.</summary>
public sealed record AudioListenerItem(string Name, bool IsPhone);

/// <summary>A contact with "Phát âm thanh" on, as the "Nghe" list shows it.</summary>
public sealed partial class AudioSourceItem(AudioViewModel owner, DeviceId deviceId, string name, bool isPhone, bool isListening)
{
    public DeviceId DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public bool IsPhone { get; } = isPhone;

    /// <summary>We listen to it now.</summary>
    public bool IsListening { get; } = isListening;

    public bool CanListen => !IsListening;

    [RelayCommand]
    private Task ListenAsync() => owner.ListenAsync(this);

    [RelayCommand]
    private Task StopListeningAsync() => owner.StopListeningAsync();

    internal bool SameAs(AudioSourceItem other) =>
        DeviceId == other.DeviceId && Name == other.Name && IsPhone == other.IsPhone && IsListening == other.IsListening;
}

/// <summary>
/// The "Âm thanh" page on the computer and the tab on the phone (docs/10-ux.md §12): this device's sound for contacts
/// that choose to listen, the contacts playing theirs, and how much delay listening allows.
/// </summary>
public sealed partial class AudioViewModel : ObservableObject
{
    private readonly AudioService audio;
    private readonly PeerDirectory directory;
    private readonly TrustStore trust;
    private readonly SettingsService settings;
    private bool refreshing;

    public AudioViewModel(AudioService audio, PeerDirectory directory, TrustStore trust, SettingsService settings)
    {
        this.audio = audio;
        this.directory = directory;
        this.trust = trust;
        this.settings = settings;
        audio.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        audio.Notice += (_, message) => Dispatcher.UIThread.Post(() => Notice?.Invoke(this, message));
        directory.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshSources);
        trust.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    /// <summary>Something the user should read: the sound stopped, a source refused. Raised on the UI thread.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>
    /// Asked before playing starts, for what the system wants first (Android: the permission to capture the phone's
    /// sound); false leaves "Phát âm thanh" off, an <see cref="InvalidOperationException"/> also says why. Null when
    /// nothing is needed.
    /// </summary>
    public Func<Task<bool>>? AllowPlaying { get; set; }

    public bool CanPlay => audio.CanPlay;

    public bool CanListen => audio.CanListen;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>Who listens right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasListeners))]
    public partial IReadOnlyList<AudioListenerItem> Listeners { get; set; } = [];

    public bool HasListeners => Listeners.Count > 0;

    /// <summary>Contacts playing their sound now, and the one we listen to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSources))]
    public partial IReadOnlyList<AudioSourceItem> Sources { get; set; } = [];

    public bool HasNoSources => Sources.Count == 0;

    /// <summary>"Đang nghe âm thanh từ PC · MSI" while it plays here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListening))]
    public partial string? ListeningText { get; set; }

    public bool IsListening => ListeningText is not null;

    /// <summary>"Độ trễ khi nghe": 0 low (games), 1 stable (music).</summary>
    public int LatencyIndex
    {
        get => settings.Current.AudioLatency == AudioLatency.Stable ? 1 : 0;
        set => settings.Update(s => s with { AudioLatency = value == 1 ? "stable" : "low" });
    }

    [RelayCommand]
    internal Task StopListeningAsync() => audio.StopListeningAsync();

    internal async Task ListenAsync(AudioSourceItem source)
    {
        try
        {
            if (audio.ListeningTo is not null)
            {
                await audio.StopListeningAsync();
            }

            await audio.ListenAsync(source.DeviceId);
        }
        catch (InvalidOperationException ex)
        {
            Notice?.Invoke(this, ex.Message);
        }
        catch (PeerApiException ex)
        {
            Notice?.Invoke(this, ex.Status switch
            {
                404 => L.F("{0} đã tắt phát âm thanh.", source.Name),
                503 => L.F("{0} đang phát cho đủ {1} máy.", source.Name, AudioService.MaxListeners),
                _ => L.F("{0} từ chối cho nghe.", source.Name),
            });
        }
        catch (Exception ex) when (ex is PeerUnreachableException or HttpRequestException)
        {
            Notice?.Invoke(this, L.F("Không liên lạc được với {0}.", source.Name));
        }
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (!refreshing)
        {
            _ = SetPlayingAsync(value);
        }
    }

    private async Task SetPlayingAsync(bool on)
    {
        try
        {
            if (on && AllowPlaying is { } allow && !await allow())
            {
                Refresh();
                return;
            }

            audio.SetPlaying(on);
        }
        catch (InvalidOperationException ex)
        {
            Refresh();
            Notice?.Invoke(this, ex.Message);
        }
    }

    private void Refresh()
    {
        refreshing = true;
        IsPlaying = audio.IsPlaying;
        refreshing = false;
        Listeners = [.. audio.Listeners.Select(l => new AudioListenerItem(NameOf(l.PeerId, null), IsPhone(l.PeerId)))];
        ListeningText = audio.ListeningTo is { } peer ? L.F("Đang nghe âm thanh từ {0}", NameOf(peer, null)) : null;
        RefreshSources();
    }

    /// <summary>Presence arrives every few seconds: the list is only replaced when what it shows changed.</summary>
    private void RefreshSources()
    {
        DeviceId? listeningTo = audio.ListeningTo;
        List<AudioSourceItem> items =
        [
            .. directory.Snapshot()
                .Where(p => (p.IsPlayingAudio || p.DeviceId == listeningTo) && trust.GetLevel(p.DeviceId) == TrustLevel.Trusted)
                .Select(p => new AudioSourceItem(this, p.DeviceId, NameOf(p.DeviceId, p), IsPhone(p.DeviceId), p.DeviceId == listeningTo)),
        ];
        items.Sort((x, y) => CultureInfo.CurrentCulture.CompareInfo.Compare(x.Name, y.Name, CompareOptions.IgnoreCase));
        if (!CanListen)
        {
            items.Clear();
        }

        if (items.Count != Sources.Count || !items.Zip(Sources).All(pair => pair.First.SameAs(pair.Second)))
        {
            Sources = items;
        }
    }

    private bool IsPhone(DeviceId id) =>
        directory.Snapshot().FirstOrDefault(p => p.DeviceId == id) is { } live
            ? live.Os.Equals("android", StringComparison.OrdinalIgnoreCase)
            : trust.Get(id)?.IsPhone == true;

    private string NameOf(DeviceId id, PeerInfo? live) =>
        trust.Get(id)?.ShownName ?? (live ?? directory.Snapshot().FirstOrDefault(p => p.DeviceId == id))?.DisplayName ?? id.ShortForm;
}
