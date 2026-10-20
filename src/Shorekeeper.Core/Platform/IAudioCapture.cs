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
    /// controlled, and the notification may offer its media keys.
    /// </summary>
    IDisposable Start(AudioFrameReader read, string title, Action stopRequested, Action<MediaKey>? keyRequested = null);
}
