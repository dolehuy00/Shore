using Microsoft.Extensions.Hosting;

namespace Shorekeeper.Engine.Hosting;

/// <summary>Initializes the engine when the generic host starts.</summary>
internal sealed class EngineHostedService(ShorekeeperEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => engine.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
