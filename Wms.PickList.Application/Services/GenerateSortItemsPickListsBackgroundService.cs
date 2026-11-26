using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wms.Contracts.HttpCurrentUser;
using Wms.Contracts.Interfaces;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Common;

namespace Wms.PickList.Application.Services;

internal sealed class GenerateSortItemsPickListsBackgroundService(IServiceScopeFactory scopeFactory, GenerateSortItemsPickListsQueue queue,
    ILogger<GenerateSortItemsPickListsBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Generate sort items pick lists background service started.");

        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(GenerateSortItemsPickListJob job, CancellationToken ct)
    {
        if (job.Orders.Count == 0)
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContextWrite>();
            var allocationService = scope.ServiceProvider.GetRequiredService<OrderInventoryAllocationService>();
            var settings = scope.ServiceProvider.GetRequiredService<IOptions<PickListSettings>>();
            var fcmService = scope.ServiceProvider.GetService<IFcmNotificationService>();
            var taskLogger = scope.ServiceProvider.GetService<ILogger<Work.Application.Tasks.Comand.AddTaskCommand.Handler>>();

            var orderIds = job.Orders.Select(o => o.OrderId).Distinct().ToList();
            var allOrderItemIds = job.Orders.SelectMany(o => o.OrderItemIds).ToHashSet();

            if (orderIds.Count == 0 || allOrderItemIds.Count == 0)
                return;

            var orders = await dbContext.WmsOrders
                .Include(o => o.WmsOrderItems.Where(i => allOrderItemIds.Contains(i.Id)))
                .Where(o => orderIds.Contains(o.Id))
                .ToListAsync(ct);

            if (orders.Count == 0)
            {
                logger.LogWarning(
                    "Sort items pick list generation skipped — no orders found for route {RouteUuid}.",
                    job.RouteUuid);
                return;
            }

            var systemUser = new SystemUser(orders[0].WarehouseId);
            var allSortItems = orders
                .SelectMany(o => o.WmsOrderItems)
                .Where(i => allOrderItemIds.Contains(i.Id))
                .ToList();

            if (allSortItems.Count == 0)
            {
                logger.LogWarning(
                    "Sort items pick list generation skipped for route {RouteUuid} — queued order item ids were not found on orders.",
                    job.RouteUuid);
                return;
            }

            var generationService = new PickListGenerationService(dbContext, systemUser, allocationService, settings, fcmService, taskLogger);
            var result = await generationService.GenerateSortItemsPickAsync(orders, allSortItems, ct);

            logger.LogInformation(
                "Background sort items pick list generation completed for route {RouteUuid}. OrdersProcessed: {OrdersProcessed}, ItemsProcessed: {ItemsProcessed}, PickListsCreated: {PickListsCreated}.",
                job.RouteUuid,
                orders.Count,
                allSortItems.Count,
                result.PickListUuids.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to generate sort items pick lists in background for route {RouteUuid}.",
                job.RouteUuid);
        }
    }
}
