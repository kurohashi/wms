using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wms.PickList.Application.Services.Cancellation;

/// <summary>
/// Background service that continuously reads order UUIDs from <see cref="PickListCancellationQueue"/>
/// and executes pick list cancellation asynchronously.
/// </summary>
internal sealed class PickListCancellationBackgroundService(
    IServiceScopeFactory scopeFactory,
    PickListCancellationQueue queue,
    ILogger<PickListCancellationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Pick list cancellation background service started.");

        await foreach (var orderUuid in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessCancellationAsync(orderUuid, stoppingToken);
        }
    }

    private async Task ProcessCancellationAsync(Guid orderUuid, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var cancellationService = scope.ServiceProvider.GetRequiredService<IPickListCancellationService>();
            await cancellationService.CancelPickListsAsync(orderUuid, ct);

            logger.LogInformation("Background pick list cancellation processed for order {OrderUuid}.", orderUuid);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing background pick list cancellation for order {OrderUuid}.", orderUuid);
        }
    }
}
