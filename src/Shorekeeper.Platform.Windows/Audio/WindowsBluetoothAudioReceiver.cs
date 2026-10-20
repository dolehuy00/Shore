using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;
using Windows.Media.Audio;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>
/// "Nhận âm thanh qua Bluetooth" through Windows' AudioPlaybackConnection (A2DP sink, Windows 10 2004 and later). A
/// phone paired with this computer only stays connected as an audio source while an app keeps a connection for it
/// open; without one it connects, finds nothing and drops. A connection kept open also takes the phone back by itself
/// when its Bluetooth comes back on.
/// </summary>
public sealed class WindowsBluetoothAudioReceiver : IBluetoothAudioReceiver, IDisposable
{
    private readonly ILogger<WindowsBluetoothAudioReceiver> logger;
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> names = [];
    private readonly Dictionary<string, AudioPlaybackConnection> connections = [];
    private readonly DeviceWatcher? watcher;

    public WindowsBluetoothAudioReceiver(ILogger<WindowsBluetoothAudioReceiver> logger)
    {
        this.logger = logger;
        if (!ApiInformation.IsTypePresent("Windows.Media.Audio.AudioPlaybackConnection"))
        {
            return;
        }

        watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());
        watcher.Added += (_, device) => Update(() => names[device.Id] = device.Name);
        watcher.Updated += (_, update) =>
        {
            if (update.Properties.TryGetValue("System.ItemNameDisplay", out object? name) && name is string shown)
            {
                Update(() => names[update.Id] = shown);
            }
        };
        watcher.Removed += (_, update) =>
        {
            Disconnect(update.Id);
            Update(() => names.Remove(update.Id));
        };
        watcher.Start();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<BluetoothAudioSource> Sources
    {
        get
        {
            lock (gate)
            {
                return
                [
                    .. names
                        .Select(pair => new BluetoothAudioSource(pair.Key, pair.Value, StateOf(pair.Key)))
                        .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase),
                ];
            }
        }
    }

    public async Task ConnectAsync(string id)
    {
        string name;
        AudioPlaybackConnection? connection;
        lock (gate)
        {
            name = names.GetValueOrDefault(id) ?? id;
            if (connections.ContainsKey(id))
            {
                return;
            }

            connection = TryCreate(id);
            if (connection is null)
            {
                throw new InvalidOperationException(L.F("Không kết nối được âm thanh Bluetooth với {0}.", name));
            }

            connections[id] = connection;
        }

        connection.StateChanged += (_, _) =>
        {
            logger.LogInformation("Bluetooth audio from {Name}: {State}", name, connection.State);
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Changed?.Invoke(this, EventArgs.Empty);
        AudioPlaybackConnectionOpenResult result;
        try
        {
            await connection.StartAsync();
            result = await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            Disconnect(id);
            logger.LogWarning(ex, "Bluetooth audio from {Name} failed", name);
            throw new InvalidOperationException(L.F("Không kết nối được âm thanh Bluetooth với {0}.", name), ex);
        }

        if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
        {
            Disconnect(id);
            logger.LogInformation("Bluetooth audio from {Name}: {Status} {Error}", name, result.Status, result.ExtendedError?.Message);
            throw new InvalidOperationException(result.Status switch
            {
                AudioPlaybackConnectionOpenResultStatus.RequestTimedOut =>
                    L.F("{0} không trả lời. Bật Bluetooth trên điện thoại, để gần máy tính rồi thử lại.", name),
                AudioPlaybackConnectionOpenResultStatus.DeniedBySystem => L.F("Windows không cho nhận âm thanh Bluetooth từ {0}.", name),
                _ => L.F("Không kết nối được âm thanh Bluetooth với {0}.", name),
            });
        }
    }

    public void Disconnect(string id)
    {
        AudioPlaybackConnection? connection;
        lock (gate)
        {
            connections.Remove(id, out connection);
        }

        if (connection is not null)
        {
            connection.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void OpenSettings() => Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });

    public void Dispose()
    {
        watcher?.Stop();
        lock (gate)
        {
            foreach (AudioPlaybackConnection connection in connections.Values)
            {
                connection.Dispose();
            }

            connections.Clear();
        }
    }

    // Despite its name, TryCreateFromId throws for an id Windows does not know (no longer paired, for one).
    private static AudioPlaybackConnection? TryCreate(string id)
    {
        try
        {
            return AudioPlaybackConnection.TryCreateFromId(id);
        }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentException or COMException)
        {
            return null;
        }
    }

    private BluetoothAudioState StateOf(string id) => connections.GetValueOrDefault(id) switch
    {
        null => BluetoothAudioState.Off,
        { State: AudioPlaybackConnectionState.Opened } => BluetoothAudioState.Connected,
        _ => BluetoothAudioState.Waiting,
    };

    private void Update(Action change)
    {
        lock (gate)
        {
            change();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
