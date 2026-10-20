namespace Shorekeeper.Core.Platform;

/// <summary>A phone paired with this computer over Bluetooth that can play into its speakers.</summary>
public enum BluetoothAudioState
{
    Off,

    /// <summary>Kept open for it: the phone connects by itself when its Bluetooth is on and in range.</summary>
    Waiting,
    Connected,
}

public sealed record BluetoothAudioSource(string Id, string Name, BluetoothAudioState State);

/// <summary>
/// "Nhận âm thanh qua Bluetooth" (Windows 10 2004 and later): this computer acts as a Bluetooth speaker for phones
/// paired with it. Plain Bluetooth audio (A2DP), so about half a second late: for music, not for games.
/// </summary>
public interface IBluetoothAudioReceiver
{
    /// <summary>Paired phones that can play into this computer, by name.</summary>
    IReadOnlyList<BluetoothAudioSource> Sources { get; }

    /// <summary>A phone was paired or removed, connected or dropped. Raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Connects to the phone and keeps the way open for it until <see cref="Disconnect"/>.</summary>
    /// <exception cref="InvalidOperationException">With the reason to show, when the phone could not be connected.</exception>
    Task ConnectAsync(string id);

    void Disconnect(string id);

    /// <summary>Opens the system's Bluetooth settings, where phones are paired.</summary>
    void OpenSettings();
}
