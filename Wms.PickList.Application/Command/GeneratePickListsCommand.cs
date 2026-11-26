using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Microsoft.EntityFrameworkCore;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;
using Wms.PickList.Application.Services;

namespace Wms.PickList.Application.Command;

/// <summary>
/// Selects pending routes/orders for pick list generation, marks those routes INITIATED,
/// and enqueues <see cref="PickListGenerationService.GenerateAsync"/> to run in the background.
/// </summary>
public sealed class GeneratePickListsCommand(List<int>? routeIds = null, int? batchSize = null)
    : ICommand<GeneratePickListsQueuedResponse>
{
    public List<int>? RouteIds { get; } = routeIds;
    public int? BatchSize { get; } = batchSize;

    public sealed class Handler(WmsDbContextWrite dbContext, IGeneratePickListsQueue generatePickListsQueue)
        : ICommandHandler<GeneratePickListsCommand, GeneratePickListsQueuedResponse>
    {
        public async Task<GeneratePickListsQueuedResponse> HandleAsync(GeneratePickListsCommand command, CancellationToken ct = default)
        {
            var effectiveBatchSize = command.BatchSize is > 0 ? command.BatchSize.Value : 3;
            List<int> targetRouteIds;

            if (command.RouteIds != null && command.RouteIds.Count > 0)
            {
                targetRouteIds = command.RouteIds;
            }
            else
            {
                var pendingRouteStatus = nameof(RouteStatusEnum.PENDING);

                targetRouteIds = await dbContext.RouteMasters
                    .Where(rm => rm.IsActive == true
                                 && rm.IsDeleted == false
                                 && rm.Status == pendingRouteStatus)
                    .OrderBy(rm => rm.CreatedDate)
                    .Take(effectiveBatchSize)
                    .Select(rm => rm.Id)
                    .ToListAsync(ct);
            }

            if (targetRouteIds.Count == 0)
            {
                return new GeneratePickListsQueuedResponse(effectiveBatchSize, Status: "IN_PROGRESS",
                    Message: "No pending routes found for pick list generation.");
            }

            var cancelOrderStatus = nameof(WmsOrderStatusEnum.CANCELLED);

            var orderIds = await (
                from rm in dbContext.RouteMasters
                join ro in dbContext.RouteOrders on rm.Id equals ro.RouteId
                where targetRouteIds.Contains(rm.Id)
                      && ro.IsActive == true
                      && ro.IsDeleted == false
                      && ro.OrderId != null
                      && ro.Order!.OrderStatus != cancelOrderStatus
                select ro.OrderId!.Value)
                .Distinct()
                .ToListAsync(ct);

            var pendingRouteStatusStr = nameof(RouteStatusEnum.PENDING);
            var initiatedRouteStatusStr = nameof(RouteStatusEnum.INITIATED);

            var routesToUpdate = await dbContext.RouteMasters
                .Where(rm => targetRouteIds.Contains(rm.Id) && rm.Status == pendingRouteStatusStr)
                .ToListAsync(ct);

            var now = DateTime.UtcNow;
            foreach (var route in routesToUpdate)
            {
                route.Status = initiatedRouteStatusStr;
                route.UpdatedDate = now;
            }

            if (routesToUpdate.Count > 0)
                await dbContext.SaveChangesAsync(ct);

            await generatePickListsQueue.EnqueueAsync(orderIds, ct);

            return new GeneratePickListsQueuedResponse(
                effectiveBatchSize,
                Status: "IN_PROGRESS",
                Message: orderIds.Count == 0
                    ? "Routes selected but no orders were queued for pick list generation."
                    : $"Pick list generation queued for {orderIds.Count} order(s) across {targetRouteIds.Count} route(s).");
        }
    }
}

public sealed record GeneratePickListsResult(
    int OrdersProcessed,
    int PickListsCreated,
    int OrdersMarkedGenerated,
    int OrdersDiscontinued,
    int DropZoneStepsExecuted,
    int WorkerAssignmentStepsExecuted,
    int ItemsFullyAllocated,
    int ItemsPartiallyAllocated,
    int ItemsUnallocated,
    int ItemsSkippedRouteFinalZone,
    int WorkersAssigned);
