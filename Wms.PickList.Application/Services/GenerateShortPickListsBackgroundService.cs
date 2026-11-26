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

internal sealed class GenerateShortPickListsBackgroundService(
    IServiceScopeFactory scopeFactory,
    GenerateShortPickListsQueue queue,
    ILogger<GenerateShortPickListsBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Generate short pick lists background service started.");

        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(GenerateShortPickListJob job, CancellationToken ct)
    {
        if (job.OrderItemIds.Count == 0)
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContextWrite>();
            var allocationService = scope.ServiceProvider.GetRequiredService<OrderInventoryAllocationService>();
            var settings = scope.ServiceProvider.GetRequiredService<IOptions<PickListSettings>>();
            var fcmService = scope.ServiceProvider.GetService<IFcmNotificationService>();
            var taskLogger = scope.ServiceProvider.GetService<ILogger<Work.Application.Tasks.Comand.AddTaskCommand.Handler>>();

            var orderItemIdSet = job.OrderItemIds.ToHashSet();

            var order = await dbContext.WmsOrders
                .Include(o => o.WmsOrderItems.Where(i => orderItemIdSet.Contains(i.Id)))
                .FirstOrDefaultAsync(o => o.Id == job.OrderId, ct);

            if (order is null)
            {
                logger.LogWarning(
                    "Short pick list generation skipped — order {OrderId} not found for route {RouteUuid}.",
                    job.OrderId,
                    job.RouteUuid);
                return;
            }

            var shortPickItems = order.WmsOrderItems
                .Where(i => orderItemIdSet.Contains(i.Id))
                .ToList();

            if (shortPickItems.Count == 0)
            {
                logger.LogWarning(
                    "Short pick list generation skipped for route {RouteUuid} — queued order item ids were not found on order {OrderId}.",
                    job.RouteUuid,
                    job.OrderId);
                return;
            }

            var shortPickItemIds = shortPickItems.Select(i => i.Id).ToList();
            var remainingQty = shortPickItems.Sum(i => Math.Max(0, i.TotalItemQuantity - (i.ConfirmedQuantity > 0 ? i.ConfirmedQuantity.Value : (i.PickedQuantity ?? 0))));
            logger.LogInformation(
                "Processing short-pick generation for route {RouteUuid}, order {OrderId}, orderUuid {OrderUuid}, itemCount {ItemCount}, remainingQty {RemainingQty}.",
                job.RouteUuid,
                order.Id,
                order.OrderUuid,
                shortPickItems.Count,
                remainingQty);

            var systemUser = new SystemUser(order!.WarehouseId);
            var generationService = new PickListGenerationService(dbContext, systemUser, allocationService, settings, fcmService, taskLogger);
            var result = await generationService.GenerateShortPickAsync(order, shortPickItems, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to generate short pick lists in background for route {RouteUuid}, order {OrderId}.",
                job.RouteUuid,
                job.OrderId);
        }
    }
}
