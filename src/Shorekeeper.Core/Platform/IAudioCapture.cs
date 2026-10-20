using Shorekeeper.Core.Audio;

namespace Shorekeeper.Core.Platform;

/// <summary>What the speakers play, for "Phát âm thanh" (Windows).</summary>
public interface IAudioCapture
{
    /// <summary>Starts capturing; <paramref name="onFrame"/> runs on a capture thread until the result is disposed.</summary>
    /// <exception cref="InvalidOperationException">The speakers cannot be captured.</exception>
    IDisposable Start(AudioFrameHandler onFrame);
}

/// <summary>Plays a computer's sound on this device, for "Nghe" (Android).</summary>
public interface IAudioOutput
{
    /// <summary>
    /// Plays until the result is disposed, calling <paramref name="read"/> for each frame at the device's pace.
    /// <paramref name="title"/> says what plays ("Đang nghe PC · MSI"); <paramref name="stopRequested"/> runs when the
    /// user stops it outside the app (the notification's button). With <paramref name="keyRequested"/> the source can be
    /// controlled, and the notification may offer its media keys. <paramref name="device"/> is an id from
    /// <see cref="IAudioOutputDevices"/>; null, or a device that is gone, plays on the default one.
    /// </summary>
    IDisposable Start(AudioFrameReader read, string title, Action stopRequested, Action<MediaKey>? keyRequested = null, string? device = null);
}

public sealed record AudioOutputDevice(string Id, string Name);

/// <summary>
/// The devices "Nghe" can play on ("Phát ra", Windows): the speakers, headphones, or a virtual cable such as VB-CABLE,
/// whose other end apps on this computer take as a microphone.
/// </summary>
public interface IAudioOutputDevices
{
    /// <summary>Those active now, by name.</summary>
    IReadOnlyList<AudioOutputDevice> GetAll();
}
