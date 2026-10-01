using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shorekeeper.Engine.Transfers;

/// <summary>
/// Loads offers and the inbox at start-up (before the API accepts requests), then periodically
/// retries undelivered offers and closes expired ones.
/// </summary>
internal sealed class TransferWorker(OfferService offers, InboxService inbox, ILogger<TransferWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await offers.LoadAsync(cancellationToken);
        await inbox.LoadAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await offers.ExpireAsync();
                    await inbox.ExpireAsync();
                    await offers.DeliverPendingAsync(null, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Transfer housekeeping failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
