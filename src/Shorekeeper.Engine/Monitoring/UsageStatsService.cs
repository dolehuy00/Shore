using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Audio;

namespace Shorekeeper.Engine.Monitoring;

/// <summary>What was sent, received, listened to and played over a period, and the messages sent and received.</summary>
public sealed record UsageCounts(
    long SentFiles, long SentBytes, long ReceivedFiles, long ReceivedBytes, long ListenSeconds, long PlaySeconds, long SentMessages = 0, long ReceivedMessages = 0)
{
    public static readonly UsageCounts Zero = new(0, 0, 0, 0, 0, 0);
}

/// <summary>What was sent to and received from one device, since counting began.</summary>
public sealed record PeerUsage(DeviceId PeerId, long SentFiles, long SentBytes, long ReceivedFiles, long ReceivedBytes);

/// <summary>"Thống kê" in Settings (docs/10-ux.md §15).</summary>
/// <param name="Since">When counting began: the first start of a version that counts.</param>
public sealed record UsageSummary(UsageCounts Today, UsageCounts Week, UsageCounts Month, UsageCounts Total, IReadOnlyList<PeerUsage> Peers, DateTimeOffset Since);

/// <summary>
/// Counts transfers and sound time (docs/06-file-transfer.md §13). Kept light: counters in memory, written every 15
/// seconds when they changed to usage.json, which holds only a line per day for the last 31 days, the totals and a
/// line per device. Sent bytes are those actually served; a file counts as sent once read to its end, as received once
/// downloaded. Listening and playing (with someone listening) are sampled on the same 15-second beat.
/// </summary>
public sealed class UsageStatsService : IHostedService, IDisposable
{
    private const int DaysKept = 31;
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    private readonly string path;
    private readonly AudioService? audio;
    private readonly TimeProvider time;
    private readonly ILogger<UsageStatsService> logger;
    private readonly Lock gate = new();
    private readonly UsageFile file;
    private ITimer? timer;
    private long lastSample;
    private bool dirty;

    public UsageStatsService(AppPaths paths, TimeProvider time, ILogger<UsageStatsService> logger, AudioService? audio = null)
    {
        path = Path.Combine(paths.DataDirectory, "usage.json");
        this.audio = audio;
        this.time = time;
        this.logger = logger;
        file = Load() ?? new UsageFile { Since = time.GetUtcNow().ToUnixTimeMilliseconds() };
        lastSample = time.GetTimestamp();
    }

    /// <summary>Raised (off the UI thread) after counters were written, at most every 15 seconds.</summary>
    public event EventHandler? Changed;

    public void AddSent(DeviceId peer, long bytes, int files) => Add(peer, c =>
    {
        c.SentBytes += bytes;
        c.SentFiles += files;
    });

    public void AddReceived(DeviceId peer, long bytes, int files) => Add(peer, c =>
    {
        c.ReceivedBytes += bytes;
        c.ReceivedFiles += files;
    });

    /// <summary>A message delivered to <paramref name="peer"/> (<paramref name="sent"/>) or newly received from it.</summary>
    public void AddMessage(DeviceId peer, bool sent) => Add(peer, c =>
    {
        if (sent)
        {
            c.SentMessages++;
        }
        else
        {
            c.ReceivedMessages++;
        }
    });

    public UsageSummary Summary()
    {
        DateTime today = time.GetLocalNow().Date;
        lock (gate)
        {
            return new UsageSummary(
                Sum(today, 1), Sum(today, 7), Sum(today, 30), file.Total.ToCounts(),
                [.. file.Peers.Select(p => new PeerUsage(DeviceId.Parse(p.Key), p.Value.SentFiles, p.Value.SentBytes, p.Value.ReceivedFiles, p.Value.ReceivedBytes))
                    .OrderByDescending(p => p.SentBytes + p.ReceivedBytes)],
                DateTimeOffset.FromUnixTimeMilliseconds(file.Since));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = time.CreateTimer(_ => Tick(), null, Every, Every);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Tick();
        return Task.CompletedTask;
    }

    public void Dispose() => timer?.Dispose();

    private void Add(DeviceId? peer, Action<Counter> add)
    {
        lock (gate)
        {
            add(Today());
            add(file.Total);
            if (peer is { } id)
            {
                string key = id.Value;
                if (!file.Peers.TryGetValue(key, out Counter? counter))
                {
                    file.Peers[key] = counter = new Counter();
                }

                add(counter);
            }

            dirty = true;
        }
    }

    /// <summary>Samples the sound and writes what changed.</summary>
    private void Tick()
    {
        long now = time.GetTimestamp();
        long seconds = (long)time.GetElapsedTime(lastSample, now).TotalSeconds;
        if (seconds > 0)
        {
            // Whole seconds are counted; the fraction carries over to the next beat.
            lastSample += seconds * time.TimestampFrequency;
            bool listening = audio?.ListeningTo is not null;
            bool playing = audio is { IsPlaying: true, Listeners.Count: > 0 };
            if (listening || playing)
            {
                Add(null, c =>
                {
                    c.ListenSeconds += listening ? seconds : 0;
                    c.PlaySeconds += playing ? seconds : 0;
                });
            }
        }

        byte[] json;
        lock (gate)
        {
            if (!dirty)
            {
                return;
            }

            dirty = false;
            string oldest = time.GetLocalNow().Date.AddDays(-DaysKept).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (string day in file.Days.Keys.Where(d => string.CompareOrdinal(d, oldest) <= 0).ToList())
            {
                file.Days.Remove(day);
            }

            json = JsonSerializer.SerializeToUtf8Bytes(file, UsageJsonContext.Default.UsageFile);
        }

        try
        {
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save usage counters");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Counter Today()
    {
        string key = time.GetLocalNow().Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!file.Days.TryGetValue(key, out Counter? counter))
        {
            file.Days[key] = counter = new Counter();
        }

        return counter;
    }

    /// <summary>The last <paramref name="days"/> days, today included.</summary>
    private UsageCounts Sum(DateTime today, int days)
    {
        string from = today.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sum = new Counter();
        foreach ((string day, Counter counter) in file.Days)
        {
            if (string.CompareOrdinal(day, from) >= 0)
            {
                sum.Add(counter);
            }
        }

        return sum.ToCounts();
    }

    private UsageFile? Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, UsageJsonContext.Default.UsageFile);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Counters are not worth refusing to start over: begin again.
            logger.LogWarning(ex, "Could not read usage counters; starting over");
            return null;
        }
    }
}

internal sealed class UsageFile
{
    /// <summary>Unix milliseconds.</summary>
    public long Since { get; set; }

    public Counter Total { get; set; } = new();

    /// <summary>By local date, "yyyy-MM-dd".</summary>
    public Dictionary<string, Counter> Days { get; set; } = [];

    /// <summary>By device id.</summary>
    public Dictionary<string, Counter> Peers { get; set; } = [];
}

internal sealed class Counter
{
    public long SentFiles { get; set; }

    public long SentBytes { get; set; }

    public long ReceivedFiles { get; set; }

    public long ReceivedBytes { get; set; }

    public long ListenSeconds { get; set; }

    public long PlaySeconds { get; set; }

    public long SentMessages { get; set; }

    public long ReceivedMessages { get; set; }

    public void Add(Counter other)
    {
        SentFiles += other.SentFiles;
        SentBytes += other.SentBytes;
        ReceivedFiles += other.ReceivedFiles;
        ReceivedBytes += other.ReceivedBytes;
        ListenSeconds += other.ListenSeconds;
        PlaySeconds += other.PlaySeconds;
        SentMessages += other.SentMessages;
        ReceivedMessages += other.ReceivedMessages;
    }

    public UsageCounts ToCounts() => new(SentFiles, SentBytes, ReceivedFiles, ReceivedBytes, ListenSeconds, PlaySeconds, SentMessages, ReceivedMessages);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(UsageFile))]
internal sealed partial class UsageJsonContext : JsonSerializerContext;
