using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows.Audio;

/// <summary>"Phát ra": the playback devices Windows has active, as named in its sound settings.</summary>
public sealed class WindowsAudioOutputDevices : IAudioOutputDevices
{
    public IReadOnlyList<AudioOutputDevice> GetAll() =>
        [.. Wasapi.ActiveSpeakers().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
}
