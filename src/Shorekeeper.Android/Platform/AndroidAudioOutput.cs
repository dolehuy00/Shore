using Android.Content;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Platform;
using AndroidProcess = Android.OS.Process;
using ForegroundService = Android.Content.PM.ForegroundService;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// "Nghe" (docs/14-android.md §3.5): another device's sound through a low-latency <see cref="AudioTrack"/>, in a
/// media-playback foreground service so it keeps playing with the screen off, holding <see cref="AudioLocks"/>.
/// </summary>
internal sealed class AndroidAudioOutput(Context context) : IAudioOutput
{
    // Three frames: room for the thread to be a little late without the device running dry (two ran dry now and then).
    private const int TrackFrames = 3;

    /// <summary>The notification's "Dừng", for the session playing now.</summary>
    internal static Action? StopRequested { get; private set; }

    public IDisposable Start(AudioFrameReader read, string title, Action stopRequested)
    {
        StopRequested = stopRequested;
        AudioPlaybackService.Start(context, title);
        return new Playing(context, read);
    }

    private sealed class Playing : IDisposable
    {
        private readonly Context context;
        private readonly AudioFrameReader read;
        private readonly AudioLocks locks;
        private readonly Thread thread;
        private volatile bool stopping;

        public Playing(Context context, AudioFrameReader read)
        {
            this.context = context;
            this.read = read;
            locks = new AudioLocks(context);
            thread = new Thread(Play) { IsBackground = true, Name = "Audio play" };
            thread.Start();
        }

        public void Dispose()
        {
            stopping = true;
            thread.Join();
            locks.Dispose();
            StopRequested = null;
            AudioPlaybackService.Stop(context);
        }

        private void Play()
        {
            AndroidProcess.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio);
            int minimum = AudioTrack.GetMinBufferSize(AudioStreamFormat.SampleRate, ChannelOut.Stereo, Encoding.Pcm16bit);
            using AudioTrack track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Game)!.SetContentType(AudioContentType.Music)!.Build()!)!
                .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(AudioStreamFormat.SampleRate)!.SetChannelMask(ChannelOut.Stereo)!.Build()!)!
                .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)!
                .SetTransferMode(AudioTrackMode.Stream)!
                .SetBufferSizeInBytes(minimum)!
                .Build()!;
            // The minimum size is what the system needs at worst; the fast mixer plays with far less.
            track.SetBufferSizeInFrames(TrackFrames * AudioStreamFormat.FrameSamples);
            short[] pcm = new short[AudioStreamFormat.FrameValues];
            track.Play();
            while (!stopping)
            {
                read(pcm);
                // Blocks until the device has room: this loop runs at the device's pace.
                track.Write(pcm, 0, pcm.Length);
            }

            track.Stop();
        }
    }
}

/// <summary>Keeps the process playing with the screen off; its notification says what plays and has "Dừng".</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class AudioPlaybackService : Service
{
    public const string ActionStop = "dev.shorekeeper.AUDIO_STOP";
    private const string ExtraTitle = "title";
    private const int NotificationId = 3;

    public override IBinder? OnBind(Intent? intent) => null;

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            AndroidAudioOutput.StopRequested?.Invoke();
            return StartCommandResult.NotSticky;
        }

        StartForeground(NotificationId, Notifications.Listening(this, intent?.GetStringExtra(ExtraTitle) ?? ""), ForegroundService.TypeMediaPlayback);
        return StartCommandResult.NotSticky;
    }

    internal static void Start(Context context, string title) =>
        context.StartForegroundService(new Intent(context, typeof(AudioPlaybackService)).PutExtra(ExtraTitle, title));

    internal static void Stop(Context context) => context.StopService(new Intent(context, typeof(AudioPlaybackService)));
}
