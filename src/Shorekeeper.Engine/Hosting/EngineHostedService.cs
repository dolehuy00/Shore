using Microsoft.Extensions.Hosting;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Hosting;

/// <summary>Initializes the engine when the generic host starts. <paramref name="names"/> comes with the peer API.</summary>
internal sealed class EngineHostedService(ShorekeeperEngine engine, PeerDirectory directory, TrustStore trust, ContactNames? names = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await engine.InitializeAsync(cancellationToken);
        // Contacts keep their phone or computer icon while offline, and follow when they rename themselves.
        directory.Changed += (_, change) =>
        {
            if (change.Kind != PeerChangeKind.Removed)
            {
                _ = trust.RecordOsAsync(change.Peer.DeviceId, change.Peer.Os);
                names?.Observe(change.Peer);
            }
        };
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
