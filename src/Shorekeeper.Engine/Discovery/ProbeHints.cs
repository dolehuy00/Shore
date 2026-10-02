using System.Net;

namespace Shorekeeper.Engine.Discovery;

/// <summary>
/// Addresses worth probing that discovery would not know otherwise: peers a contact told us about (PEX)
/// are probed for a while, configured IP ranges (Subnet Probe) are queued and sent once at a limited rate.
/// </summary>
public sealed class ProbeHints(TimeProvider timeProvider)
{
    private readonly Lock gate = new();
    private readonly Dictionary<IPEndPoint, DateTimeOffset> hints = [];
    private readonly Queue<IPEndPoint> queue = new();

    /// <summary>Probe <paramref name="endpoint"/> with every probe round until <paramref name="duration"/> has passed.</summary>
    public void Add(IPEndPoint endpoint, TimeSpan duration)
    {
        lock (gate)
        {
            hints[endpoint] = timeProvider.GetUtcNow() + duration;
        }
    }

    public IReadOnlyList<IPEndPoint> GetActive()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (gate)
        {
            foreach (IPEndPoint expired in hints.Where(h => h.Value <= now).Select(h => h.Key).ToList())
            {
                hints.Remove(expired);
            }

            return [.. hints.Keys];
        }
    }

    /// <summary>Probe each endpoint once; a scan already waiting is replaced.</summary>
    public void Enqueue(IEnumerable<IPEndPoint> endpoints)
    {
        lock (gate)
        {
            queue.Clear();
            foreach (IPEndPoint endpoint in endpoints)
            {
                queue.Enqueue(endpoint);
            }
        }
    }

    public IReadOnlyList<IPEndPoint> Dequeue(int max)
    {
        lock (gate)
        {
            var batch = new List<IPEndPoint>(Math.Min(max, queue.Count));
            while (batch.Count < max && queue.TryDequeue(out IPEndPoint? endpoint))
            {
                batch.Add(endpoint);
            }

            return batch;
        }
    }
}
