using Microsoft.Extensions.Hosting;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Hosting;

/// <summary>Initializes the engine when the generic host starts.</summary>
internal sealed class EngineHostedService(ShorekeeperEngine engine, PeerDirectory directory, TrustStore trust) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await engine.InitializeAsync(cancellationToken);
        // Contacts keep their phone or computer icon while offline.
        directory.Changed += (_, change) =>
        {
            if (change.Kind != PeerChangeKind.Removed)
            {
                _ = trust.RecordOsAsync(change.Peer.DeviceId, change.Peer.Os);
            }
        };
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
