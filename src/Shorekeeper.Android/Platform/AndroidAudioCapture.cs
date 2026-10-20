using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Shorekeeper.Core.Audio;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using AndroidProcess = Android.OS.Process;
using ForegroundService = Android.Content.PM.ForegroundService;

namespace Shorekeeper.Android.Platform;

/// <summary>
/// "Phát âm thanh" on a phone (docs/14-android.md §3.6): what other apps play, through Android's playback capture.
/// It needs the microphone permission and, each time it is turned on, the user's consent to a media projection, held
/// by <see cref="AudioCaptureService"/> until it is turned off. Only media, game and unknown sound can be captured,
/// and apps may forbid it; our own playback ("Nghe") is left out so two phones never echo each other.
/// With "Nguồn tiếng: Micro" it is the microphone instead (the phone as a microphone), which needs the permission only.
/// </summary>
internal sealed class AndroidAudioCapture(Context context) : IAudioCapture
{
    public IDisposable Start(AudioFrameHandler onFrame)
    {
        if (PlaybackCapture.Microphone)
        {
            return StartMicrophone(onFrame);
        }

        if (PlaybackCapture.Projection is not { } projection)
        {
            throw new InvalidOperationException(L.T("Chưa được phép thu âm thanh của điện thoại."));
        }

        AudioRecord record;
        try
        {
            AudioPlaybackCaptureConfiguration config = new AudioPlaybackCaptureConfiguration.Builder(projection)
                .AddMatchingUsage(AudioUsageKind.Media)!
                .AddMatchingUsage(AudioUsageKind.Game)!
                .AddMatchingUsage(AudioUsageKind.Unknown)!
                .ExcludeUid(AndroidProcess.MyUid())!
                .Build()!;
            int minimum = AudioRecord.GetMinBufferSize(AudioStreamFormat.SampleRate, ChannelIn.Stereo, Encoding.Pcm16bit);
            record = new AudioRecord.Builder()
                .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(AudioStreamFormat.SampleRate)!.SetChannelMask((ChannelOut)(int)ChannelIn.Stereo)!.Build()!)!
                .SetBufferSizeInBytes(Math.Max(minimum, 4 * AudioStreamFormat.FrameValues * sizeof(short)))!
                .SetAudioPlaybackCaptureConfig(config)!
                .Build()!;
        }
        catch (Java.Lang.Exception ex)
        {
            throw new InvalidOperationException(L.T("Không thu được âm thanh của điện thoại."), ex);
        }

        return new Capturing(context, record, onFrame, mono: false);
    }

    /// <summary>The microphone, mono, its noise suppressed where the phone can: both channels get it.</summary>
    private Capturing StartMicrophone(AudioFrameHandler onFrame)
    {
        AudioRecord record;
        try
        {
            int minimum = AudioRecord.GetMinBufferSize(AudioStreamFormat.SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
            record = new AudioRecord.Builder()
                .SetAudioSource(AudioSource.Mic)!
                .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(AudioStreamFormat.SampleRate)!.SetChannelMask((ChannelOut)(int)ChannelIn.Mono)!.Build()!)!
                .SetBufferSizeInBytes(Math.Max(minimum, 4 * AudioStreamFormat.FrameSamples * sizeof(short)))!
                .Build()!;
        }
        catch (Java.Lang.Exception ex)
        {
            throw new InvalidOperationException(L.T("Không thu được tiếng micro của điện thoại."), ex);
        }

        return new Capturing(context, record, onFrame, mono: true);
    }

    private sealed class Capturing : IDisposable
    {
        private readonly AudioRecord record;
        private readonly AudioFrameHandler onFrame;
        private readonly bool mono;
        private readonly NoiseSuppressor? noise;
        private readonly AudioLocks locks;
        private readonly Thread thread;
        private volatile bool stopping;

        public Capturing(Context context, AudioRecord record, AudioFrameHandler onFrame, bool mono)
        {
            this.record = record;
            this.onFrame = onFrame;
            this.mono = mono;
            noise = mono && NoiseSuppressor.IsAvailable ? NoiseSuppressor.Create(record.AudioSessionId) : null;
            noise?.SetEnabled(true);
            locks = new AudioLocks(context);
            thread = new Thread(Run) { IsBackground = true, Name = "Audio capture" };
            thread.Start();
        }

        public void Dispose()
        {
            stopping = true;
            thread.Join();
            noise?.Release();
            record.Release();
            locks.Dispose();
        }

        private void Run()
        {
            AndroidProcess.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio);
            short[] pcm = new short[mono ? AudioStreamFormat.FrameSamples : AudioStreamFormat.FrameValues];
            float[] frame = new float[AudioStreamFormat.FrameValues];
            record.StartRecording();
            while (!stopping)
            {
                int filled = 0;
                while (filled < pcm.Length && !stopping)
                {
                    // Blocks until the system has the samples: this loop runs at the capture's pace.
                    int read = record.Read(pcm, filled, pcm.Length - filled);
                    if (read < 0)
                    {
                        return;
                    }

                    filled += read;
                }

                if (filled == pcm.Length)
                {
                    for (int i = 0; i < frame.Length; i++)
                    {
                        frame[i] = pcm[mono ? i / 2 : i] / 32768f;
                    }

                    onFrame(frame);
                }
            }

            record.Stop();
        }
    }
}

/// <summary>
/// Asking for what capturing the phone needs: the microphone permission, then the media projection playback capture
/// runs on, or, for the microphone, only the service that keeps it recording with the screen off.
/// </summary>
internal static class PlaybackCapture
{
    private const int MicrophoneRequest = 20;
    private const int ProjectionRequest = 21;

    private static TaskCompletionSource<bool>? microphone;
    private static TaskCompletionSource<bool>? consent;

    /// <summary>Set while "Phát âm thanh" is on, playing what apps play.</summary>
    public static MediaProjection? Projection { get; private set; }

    /// <summary>"Phát âm thanh" is on, playing the microphone.</summary>
    public static bool Microphone { get; private set; }

    /// <summary>The user stopped it from outside the app: the notification's "Dừng" or the system's cast tile.</summary>
    public static event Action? Stopped;

    /// <summary>
    /// Asks for the microphone permission (once) and, for what apps play, the consent to capture (each time); the
    /// microphone needs only the permission.
    /// </summary>
    /// <returns>False when the user declined to capture this time.</returns>
    /// <exception cref="InvalidOperationException">The microphone permission was refused.</exception>
    public static async Task<bool> RequestAsync(Activity activity, bool microphoneSource)
    {
        if (activity.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            microphone = new TaskCompletionSource<bool>();
            activity.RequestPermissions([global::Android.Manifest.Permission.RecordAudio], MicrophoneRequest);
            if (!await microphone.Task)
            {
                // Refused twice, Android stops asking: only the app's settings can grant it then.
                throw new InvalidOperationException(L.T("Phát âm thanh cần quyền micrô. Nếu đã từ chối, bật lại trong Cài đặt → Ứng dụng → Shorekeeper → Quyền."));
            }
        }

        if (microphoneSource)
        {
            consent = new TaskCompletionSource<bool>();
            AudioCaptureService.StartMicrophone(activity);
            return await consent.Task;
        }

        var manager = (MediaProjectionManager)activity.GetSystemService(Context.MediaProjectionService)!;
        // The whole display: sharing a single app (Android 14+) would capture only that app's sound.
        Intent intent = OperatingSystem.IsAndroidVersionAtLeast(34)
            ? manager.CreateScreenCaptureIntent(MediaProjectionConfig.CreateConfigForDefaultDisplay())
            : manager.CreateScreenCaptureIntent();
        consent = new TaskCompletionSource<bool>();
        activity.StartActivityForResult(intent, ProjectionRequest);
        return await consent.Task;
    }

    /// <returns>Whether the request was ours.</returns>
    public static bool OnPermissionResult(int requestCode, Permission[] grantResults)
    {
        if (requestCode != MicrophoneRequest)
        {
            return false;
        }

        microphone?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
        return true;
    }

    /// <returns>Whether the request was ours.</returns>
    public static bool OnActivityResult(Context context, int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != ProjectionRequest)
        {
            return false;
        }

        if (resultCode != Result.Ok || data is null)
        {
            consent?.TrySetResult(false);
        }
        else
        {
            // Android 14+ hands out the projection only to a running mediaProjection service.
            AudioCaptureService.Start(context, (int)resultCode, data);
        }

        return true;
    }

    /// <summary>Called by the service once it holds the projection.</summary>
    internal static void Started(MediaProjection? projection)
    {
        Projection = projection;
        consent?.TrySetResult(projection is not null);
    }

    /// <summary>Called by the service once it runs for the microphone.</summary>
    internal static void MicrophoneStarted()
    {
        Microphone = true;
        consent?.TrySetResult(true);
    }

    /// <summary>"Phát âm thanh" went off: give the projection back and end the service.</summary>
    public static void Release(Context context)
    {
        if (Microphone)
        {
            Microphone = false;
            AudioCaptureService.Stop(context);
        }

        if (Projection is not { } projection)
        {
            return;
        }

        Projection = null;
        projection.Stop();
        AudioCaptureService.Stop(context);
    }

    internal static void OnStopped() => Stopped?.Invoke();
}

/// <summary>
/// Holds the media projection, or keeps the microphone recording, while "Phát âm thanh" is on; its notification says
/// so and has "Dừng".
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection | ForegroundService.TypeMicrophone)]
public sealed class AudioCaptureService : Service
{
    public const string ActionStop = "dev.shorekeeper.AUDIO_CAPTURE_STOP";
    private const string ActionMicrophone = "dev.shorekeeper.AUDIO_CAPTURE_MICROPHONE";
    private const string ExtraResultCode = "resultCode";
    private const string ExtraData = "data";
    private const int NotificationId = 4;

    public override IBinder? OnBind(Intent? intent) => null;

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            PlaybackCapture.OnStopped();
            return StartCommandResult.NotSticky;
        }

        if (intent?.Action == ActionMicrophone)
        {
            // Android 10 has no microphone type and does not ask for one.
            Notification notification = Notifications.Playing(this, microphone: true);
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }

            PlaybackCapture.MicrophoneStarted();
            return StartCommandResult.NotSticky;
        }

        StartForeground(NotificationId, Notifications.Playing(this, microphone: false), ForegroundService.TypeMediaProjection);
        MediaProjection? projection = null;
        if (Data(intent) is { } data)
        {
            var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            projection = manager.GetMediaProjection(intent!.GetIntExtra(ExtraResultCode, 0), data);
            projection?.RegisterCallback(new Callback(), new Handler(Looper.MainLooper!));
        }

        PlaybackCapture.Started(projection);
        if (projection is null)
        {
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }

    internal static void Start(Context context, int resultCode, Intent data) =>
        context.StartForegroundService(new Intent(context, typeof(AudioCaptureService)).PutExtra(ExtraResultCode, resultCode).PutExtra(ExtraData, data));

    /// <summary>Started while the app is in front: Android lets only such a service use the microphone.</summary>
    internal static void StartMicrophone(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(AudioCaptureService)).SetAction(ActionMicrophone));

    internal static void Stop(Context context) => context.StopService(new Intent(context, typeof(AudioCaptureService)));

    private static Intent? Data(Intent? intent) =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent?.GetParcelableExtra(ExtraData, Java.Lang.Class.FromType(typeof(Intent))) as Intent
#pragma warning disable CA1422 // The typed overload only exists from API 33.
            : intent?.GetParcelableExtra(ExtraData) as Intent;
#pragma warning restore CA1422

    /// <summary>The system ended the projection, e.g. from its cast tile.</summary>
    private sealed class Callback : MediaProjection.Callback
    {
        public override void OnStop() => PlaybackCapture.OnStopped();
    }
}
