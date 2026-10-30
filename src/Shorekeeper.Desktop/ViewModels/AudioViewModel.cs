using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Identity;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Core.Trust;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Audio;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Desktop.ViewModels;

/// <summary>A device listening to ours.</summary>
public sealed record AudioListenerItem(string Name, bool IsPhone);

/// <summary>A phone paired over Bluetooth, as "Nhận âm thanh qua Bluetooth" lists it.</summary>
public sealed partial class BluetoothSourceItem(AudioViewModel owner, BluetoothAudioSource source)
{
    public string Name => source.Name;

    public bool IsOff => source.State == BluetoothAudioState.Off;

    /// <summary>Kept open, the phone not connected (yet): it connects by itself when its Bluetooth is on.</summary>
    public bool IsWaiting => source.State == BluetoothAudioState.Waiting;

    public bool IsConnected => source.State == BluetoothAudioState.Connected;

    [RelayCommand]
    private Task ConnectAsync() => owner.ConnectBluetoothAsync(source);

    [RelayCommand]
    private void Disconnect() => owner.DisconnectBluetooth(source);
}

/// <summary>A line of "Thống kê": what is measured, and its value now.</summary>
public sealed record AudioStatItem(string Label, string Value);

/// <summary>What <see cref="AudioViewModel.AskPassword"/> shows; <paramref name="Masked"/> hides what is typed.</summary>
public sealed record PasswordQuestion(string Title, string Label, string OkText, bool Masked);

/// <summary>A contact with "Phát âm thanh" on, as the "Nghe" list shows it.</summary>
public sealed partial class AudioSourceItem(AudioViewModel owner, DeviceId deviceId, string name, bool isPhone, bool isListening, bool isLocked)
{
    public DeviceId DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public bool IsPhone { get; } = isPhone;

    /// <summary>We listen to it now.</summary>
    public bool IsListening { get; } = isListening;

    public bool CanListen => !IsListening;

    /// <summary>Listening needs its password (asked once, then remembered).</summary>
    public bool IsLocked { get; } = isLocked;

    [RelayCommand]
    private Task ListenAsync() => owner.ListenAsync(this);

    [RelayCommand]
    private Task StopListeningAsync() => owner.StopListeningAsync();

    internal bool SameAs(AudioSourceItem other) =>
        DeviceId == other.DeviceId && Name == other.Name && IsPhone == other.IsPhone && IsListening == other.IsListening && IsLocked == other.IsLocked;
}

/// <summary>
/// The "Âm thanh" page on the computer and the tab on the phone (docs/10-ux.md §12): this device's sound for contacts
/// that choose to listen (optionally with a password), the contacts playing theirs, and how much delay listening allows;
/// on Windows also phones playing into the computer over Bluetooth.
/// </summary>
public sealed partial class AudioViewModel : ObservableObject
{
    private readonly AudioService audio;
    private readonly PeerDirectory directory;
    private readonly TrustStore trust;
    private readonly PeerNaming naming;
    private readonly SettingsService settings;
    private readonly IBluetoothAudioReceiver? bluetooth;
    private readonly IAudioOutputDevices? outputDevices;
    private IReadOnlyList<AudioOutputDevice> outputs = [];
    private bool refreshing;

    /// <param name="bluetooth">Null where the platform cannot receive Bluetooth audio (Android, Linux, macOS).</param>
    /// <param name="outputDevices">Null where "Nghe" always plays on the default device (Android).</param>
    public AudioViewModel(
        AudioService audio, PeerDirectory directory, TrustStore trust, SettingsService settings, PeerNaming naming,
        IBluetoothAudioReceiver? bluetooth = null, IAudioOutputDevices? outputDevices = null)
    {
        this.naming = naming;
        this.audio = audio;
        this.directory = directory;
        this.trust = trust;
        this.settings = settings;
        this.bluetooth = bluetooth;
        this.outputDevices = outputDevices;
        RefreshOutputDevices();
        if (bluetooth is not null)
        {
            bluetooth.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshBluetooth);
            RefreshBluetooth();
        }

        audio.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        audio.Notice += (_, message) => Dispatcher.UIThread.Post(() => Notice?.Invoke(this, message));
        audio.StatsChanged += (_, stats) => Dispatcher.UIThread.Post(() => ShowStats(stats));
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

    /// <summary>Asks the user for a password; null when cancelled. Set by the platform's views.</summary>
    public Func<PasswordQuestion, Task<string?>>? AskPassword { get; set; }

    public bool CanPlay => audio.CanPlay;

    public bool CanListen => audio.CanListen;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>Who listens right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasListeners))]
    public partial IReadOnlyList<AudioListenerItem> Listeners { get; set; } = [];

    public bool HasListeners => Listeners.Count > 0;

    /// <summary>Listeners must give the password (set on this page).</summary>
    [ObservableProperty]
    public partial bool HasPassword { get; set; }

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

    /// <summary>"Điều khiển PC · MSI": the device we listen to lets us press its media keys; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanControl))]
    public partial string? ControlTitle { get; set; }

    public bool CanControl => ControlTitle is not null;

    /// <summary>
    /// "Âm lượng trên máy này" while listening, in percent: how loud this device plays that source, apart from the
    /// system volume and from the source itself (others listening hear no change). Remembered per source.
    /// </summary>
    public double ListenVolume
    {
        get => audio.ListenVolume;
        set
        {
            audio.SetListenVolume((int)Math.Round(value));
            OnListenVolumeChanged();
        }
    }

    public string ListenVolumeText => $"{audio.ListenVolume}%";

    public bool IsListenMuted => audio.ListenVolume == 0;

    private void OnListenVolumeChanged()
    {
        OnPropertyChanged(nameof(ListenVolume));
        OnPropertyChanged(nameof(ListenVolumeText));
        OnPropertyChanged(nameof(IsListenMuted));
    }

    /// <summary>"Nguồn tiếng" is shown: the phone can play its microphone instead of what apps play (Android).</summary>
    public bool CanChooseSource { get; init; }

    /// <summary>"Nguồn tiếng": 0 what apps play, 1 the microphone. Changed while not playing.</summary>
    public int SourceIndex
    {
        get => settings.Current.AudioFromMicrophone ? 1 : 0;
        set
        {
            settings.Update(s => s with { AudioSource = value == 1 ? "mic" : null });
            OnPropertyChanged();
        }
    }

    /// <summary>"Phát ra" is shown (Windows).</summary>
    public bool CanChooseOutput => outputDevices is not null;

    /// <summary>"Phát ra": the default speakers first, then the active devices, then the chosen one if it is gone.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> OutputChoices { get; set; } = [];

    public int OutputIndex
    {
        get
        {
            if (settings.Current.AudioOutputDevice is not { } chosen)
            {
                return 0;
            }

            int index = outputs.ToList().FindIndex(d => d.Id == chosen);
            return index >= 0 ? index + 1 : OutputChoices.Count - 1;
        }
        set
        {
            // The last entry may be the chosen device that is gone: picking it changes nothing.
            if (value >= 0 && value <= outputs.Count && value != OutputIndex)
            {
                settings.Update(s => s with { AudioOutputDevice = value == 0 ? null : outputs[value - 1].Id });
            }
        }
    }

    /// <summary>Devices come and go (headphones, a virtual cable installed): read again when the list opens.</summary>
    public void RefreshOutputDevices()
    {
        if (outputDevices is null)
        {
            return;
        }

        IReadOnlyList<AudioOutputDevice> now;
        try
        {
            now = outputDevices.GetAll();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            now = [];
        }

        bool missing = settings.Current.AudioOutputDevice is { } chosen && now.All(d => d.Id != chosen);
        List<string> choices = [L.T("Loa mặc định của Windows"), .. now.Select(d => d.Name)];
        if (missing)
        {
            choices.Add(L.T("Thiết bị đã chọn (đang không có: phát ra loa mặc định)"));
        }

        if (!choices.SequenceEqual(OutputChoices))
        {
            outputs = now;
            OutputChoices = choices;
            OnPropertyChanged(nameof(OutputIndex));
        }
    }

    /// <summary>"Nhận âm thanh qua Bluetooth" is shown (Windows).</summary>
    public bool CanReceiveBluetooth => bluetooth is not null;

    /// <summary>Phones paired with this computer over Bluetooth.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBluetoothSources))]
    public partial IReadOnlyList<BluetoothSourceItem> BluetoothSources { get; set; } = [];

    public bool HasNoBluetoothSources => BluetoothSources.Count == 0;

    /// <summary>"Thống kê" while listening, every second: ping, buffer, losses.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AudioStatItem> Stats { get; set; } = [];

    /// <summary>The state in one line above <see cref="Stats"/>: "Tốt", "Đang đo…", or what goes wrong.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>The network falters but nothing is missed: the buffer makes up for it.</summary>
    [ObservableProperty]
    public partial bool IsStatusWarning { get; set; }

    /// <summary>Sound went missing lately.</summary>
    [ObservableProperty]
    public partial bool IsStatusBad { get; set; }

    /// <summary>"Độ trễ khi nghe": 0 low (games), 1 stable (music), 2 preserve (never a gap, delay grows instead).</summary>
    public int LatencyIndex
    {
        get => (int)settings.Current.AudioLatency;
        set => settings.Update(s => s with { AudioLatency = value switch { 1 => "stable", 2 => "preserve", _ => "low" } });
    }

    [RelayCommand]
    internal Task StopListeningAsync() => audio.StopListeningAsync();

    /// <summary>"Đặt mật khẩu…" / "Đổi…": shown while typed, as it is meant to be told to the listeners.</summary>
    [RelayCommand]
    private async Task SetPasswordAsync()
    {
        if (AskPassword is { } ask
            && await ask(new PasswordQuestion(L.T("Mật khẩu nghe"), L.T("Máy muốn nghe phải nhập mật khẩu này một lần, sau đó máy đó tự nhớ:"), L.T("Lưu"), Masked: false))
                is { } typed && typed.Trim() is { Length: > 0 } password)
        {
            audio.SetPassword(password);
        }
    }

    [RelayCommand]
    private void RemovePassword() => audio.SetPassword(null);

    [RelayCommand]
    private void OpenBluetoothSettings() => bluetooth?.OpenSettings();

    [RelayCommand]
    private Task PlayPauseAsync() => PressAsync(MediaKey.PlayPause);

    [RelayCommand]
    private Task NextAsync() => PressAsync(MediaKey.Next);

    [RelayCommand]
    private Task PreviousAsync() => PressAsync(MediaKey.Previous);

    [RelayCommand]
    private Task VolumeUpAsync() => PressAsync(MediaKey.VolumeUp);

    [RelayCommand]
    private Task VolumeDownAsync() => PressAsync(MediaKey.VolumeDown);

    [RelayCommand]
    private Task MuteAsync() => PressAsync(MediaKey.Mute);

    /// <summary>"Điều khiển": a media key on the device we listen to.</summary>
    private async Task PressAsync(MediaKey key)
    {
        try
        {
            await audio.PressAsync(key);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PeerUnreachableException or PeerApiException or HttpRequestException)
        {
            Notice?.Invoke(this, L.T("Không điều khiển được máy đang phát."));
        }
    }

    internal async Task ConnectBluetoothAsync(BluetoothAudioSource source)
    {
        try
        {
            await bluetooth!.ConnectAsync(source.Id);
        }
        catch (InvalidOperationException ex)
        {
            Notice?.Invoke(this, ex.Message);
        }
    }

    internal void DisconnectBluetooth(BluetoothAudioSource source) => bluetooth?.Disconnect(source.Id);

    private void RefreshBluetooth() => BluetoothSources = [.. bluetooth!.Sources.Select(s => new BluetoothSourceItem(this, s))];

    /// <param name="password">Typed by the user; null sends the one remembered for this source, if any.</param>
    internal async Task ListenAsync(AudioSourceItem source, string? password = null)
    {
        try
        {
            if (audio.ListeningTo is not null)
            {
                await audio.StopListeningAsync();
            }

            await audio.ListenAsync(source.DeviceId, password);
        }
        catch (InvalidOperationException ex)
        {
            Notice?.Invoke(this, ex.Message);
        }
        catch (PeerApiException ex) when (ex.Code is ApiErrorCodes.PasswordRequired or ApiErrorCodes.WrongPassword)
        {
            string label = ex.Code == ApiErrorCodes.PasswordRequired ? L.F("{0} cần mật khẩu để nghe:", source.Name)
                : password is null ? L.F("{0} đã đổi mật khẩu. Nhập mật khẩu mới:", source.Name)
                : L.T("Sai mật khẩu. Nhập lại:");
            if (AskPassword is { } ask
                && await ask(new PasswordQuestion(L.F("Nghe {0}", source.Name), label, L.T("Nghe"), Masked: true)) is { } typed
                && typed.Trim() is { Length: > 0 } retry)
            {
                await ListenAsync(source, retry);
            }
        }
        catch (PeerApiException ex)
        {
            Notice?.Invoke(this, ex.Status switch
            {
                404 => L.F("{0} đã tắt phát âm thanh.", source.Name),
                429 => L.F("Nhập sai mật khẩu của {0} quá nhiều lần. Thử lại sau một phút.", source.Name),
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
        HasPassword = audio.HasPassword;
        Listeners = [.. audio.Listeners.Select(l => new AudioListenerItem(NameOf(l.PeerId, null), IsPhone(l.PeerId)))];
        ListeningText = audio.ListeningTo is { } peer ? L.F("Đang nghe âm thanh từ {0}", NameOf(peer, null)) : null;
        ControlTitle = audio.ListeningTo is { } source && audio.CanControlListened ? L.F("Điều khiển {0}", NameOf(source, null)) : null;
        OnListenVolumeChanged();
        if (!IsListening)
        {
            Stats = [];
            StatusText = null;
        }
        else if (StatusText is null)
        {
            StatusText = L.T("Đang đo…");
            IsStatusWarning = IsStatusBad = false;
        }

        RefreshSources();
    }

    private void ShowStats(AudioListeningStats stats)
    {
        if (!IsListening)
        {
            return;
        }

        (StatusText, IsStatusWarning, IsStatusBad) = stats switch
        {
            { RecentGapMs: > 0 } => (L.F("Mạng kém: hụt {0} ms tiếng trong 10 giây qua", stats.RecentGapMs), false, true),
            { LossPercent: > 0 } => (L.T("Mạng đang mất gói, đã bù kịp"), true, false),
            _ when stats.TargetMs > stats.BaseMs => (L.T("Mạng vừa chập chờn: đang giữ thêm âm thanh trong bộ đệm"), true, false),
            _ => (L.T("Tốt"), false, false),
        };
        Stats =
        [
            new(L.T("Chế độ"), stats.Latency switch
            {
                AudioLatency.Stable => L.T("Ổn định"),
                AudioLatency.Preserve => L.T("Bảo toàn"),
                _ => L.T("Thấp"),
            }),
            new(L.T("Ping"), stats.Ping is { } ping ? Milliseconds(ping.TotalMilliseconds) : "—"),
            new(L.T("Bộ đệm"), L.F("{0} ms (mục tiêu {1} ms, gốc {2} ms)", stats.BufferMs, stats.TargetMs, stats.BaseMs)),
            new(L.T("Dao động (jitter)"), Milliseconds(stats.JitterMs)),
            new(L.T("Mất gói (10 giây qua)"), L.F("{0:0.#} %", stats.LossPercent)),
            new(L.T("Gói mất từ lúc nghe"), L.F("{0} (bù được {1})", stats.Lost, stats.Saved)),
            new(L.T("Hụt tiếng từ lúc nghe"), L.F("{0} ms", stats.GapMs)),
            new(L.T("Băng thông"), L.F("{0} kbps", stats.Kbps)),
        ];
    }

    private static string Milliseconds(double ms) => ms < 1 ? "< 1 ms" : L.F("{0} ms", (int)Math.Round(ms));

    /// <summary>Presence arrives every few seconds: the list is only replaced when what it shows changed.</summary>
    private void RefreshSources()
    {
        DeviceId? listeningTo = audio.ListeningTo;
        List<AudioSourceItem> items =
        [
            .. directory.Snapshot()
                .Where(p => (p.IsPlayingAudio || p.DeviceId == listeningTo) && trust.GetLevel(p.DeviceId) == TrustLevel.Trusted)
                .Select(p => new AudioSourceItem(this, p.DeviceId, NameOf(p.DeviceId, p), IsPhone(p.DeviceId), p.DeviceId == listeningTo, p.IsAudioLocked)),
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

    private string NameOf(DeviceId id, PeerInfo? live) => naming.Of(id, live?.DisplayName);
}
