using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Asn1.X9;
using Wms.Contracts.HttpCurrentUser;
using Wms.Contracts.Interfaces;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Common;

namespace Wms.PickList.Application.Services;

/// <summary>
/// Runs pick list generation (<see cref="PickListGenerationService.GenerateAsync"/>)
/// for order IDs previously selected by <c>GeneratePickListsCommand</c>.
/// </summary>
internal sealed class GeneratePickListsBackgroundService(
    IServiceScopeFactory scopeFactory,
    GeneratePickListsQueue queue,
    ILogger<GeneratePickListsBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Generate pick lists background service started.");

        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(GeneratePickListsJob job, CancellationToken ct)
    {
        if (job.OrderIds.Count == 0)
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContextWrite>();
            var allocationService = scope.ServiceProvider.GetRequiredService<OrderInventoryAllocationService>();
            var settings = scope.ServiceProvider.GetRequiredService<IOptions<PickListSettings>>();
            var fcmService = scope.ServiceProvider.GetService<IFcmNotificationService>();
            var taskLogger = scope.ServiceProvider.GetService<ILogger<Work.Application.Tasks.Comand.AddTaskCommand.Handler>>();

            var orders = await dbContext.WmsOrders
                .Include(o => o.WmsOrderItems)
                .Where(o => job.OrderIds.Contains(o.Id))
                .OrderBy(o => o.DeliveryDateTime)
                .ToListAsync(ct);

            if (orders.Any())
            {
                var systemUser = new SystemUser(orders[0].WarehouseId);

                var generationService = new PickListGenerationService(dbContext, systemUser, allocationService, settings, fcmService, taskLogger);
                var runResult = await generationService.GenerateAsync(orders, ct);

                logger.LogInformation(
                    "Background pick list generation completed. OrdersQueued: {OrdersQueued}, OrdersProcessed: {OrdersProcessed}, PickListsCreated: {PickListsCreated}.",
                    job.OrderIds.Count,
                    runResult.Result.OrdersProcessed,
                    runResult.Result.PickListsCreated);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to generate pick lists in background for {OrderCount} order(s).",
                job.OrderIds.Count);
        }
    }
}
