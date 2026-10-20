using Microsoft.Extensions.Logging.Abstractions;
using Shorekeeper.Core.Platform;
using Shorekeeper.Platform.Windows.Audio;

namespace Shorekeeper.Platform.Windows.Tests;

public sealed class WindowsBluetoothAudioReceiverTests
{
    [Fact]
    public void Lists_paired_phones_none_connected_until_asked()
    {
        // With or without a Bluetooth adapter: nothing connects by itself.
        using var receiver = new WindowsBluetoothAudioReceiver(NullLogger<WindowsBluetoothAudioReceiver>.Instance);

        Assert.All(receiver.Sources, source => Assert.Equal(BluetoothAudioState.Off, source.State));
    }

    [Fact]
    public async Task An_unknown_device_cannot_be_connected()
    {
        using var receiver = new WindowsBluetoothAudioReceiver(NullLogger<WindowsBluetoothAudioReceiver>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.ConnectAsync("not-a-device"));
        Assert.DoesNotContain(receiver.Sources, source => source.State != BluetoothAudioState.Off);
    }
}
