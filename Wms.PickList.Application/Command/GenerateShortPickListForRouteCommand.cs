using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.PickList.Application.Dto;
using Wms.PickList.Application.Services;
using Wms.Persistence.DbContexts;
using Wms.Contracts.Interfaces;
using Wms.Domain.Entities;
using Dit.Shared.Exceptions;
using Wms.Domain.Enums;

namespace Wms.PickList.Application.Command;

/// <summary>
/// Validates short-pick items for a route, marks the route as requested,
/// and enqueues short-pick pick list generation to run in the background.
/// </summary>
public sealed class GenerateShortPickListForRouteCommand(Guid routeUuid) : ICommand<ShortPickListGenerationMessageResponse>
{
    public const string InProgressMessage = "Short picklist generation is in progress.";

    public Guid RouteUuid { get; } = routeUuid;

    public sealed class Handler(WmsDbContextWrite dbContext, IGenerateShortPickListsQueue generateShortPickListsQueue, ILogger<Handler> logger, OrderInventoryAllocationService allocationService, ICurrentUser currentUser)
        : ICommandHandler<GenerateShortPickListForRouteCommand, ShortPickListGenerationMessageResponse>
    {
        public async Task<ShortPickListGenerationMessageResponse> HandleAsync(GenerateShortPickListForRouteCommand command, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0; if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

            if (command.RouteUuid == Guid.Empty)
                throw new BusinessRuleException("RouteUuid cannot be empty.");

            var routeMaster = await dbContext.RouteMasters
                .FirstOrDefaultAsync(rm => rm.RouteUuid == command.RouteUuid && rm.WarehouseId == warehouseId, ct)
                ?? throw new BusinessRuleException($"Route for {command.RouteUuid} not found in the user's warehouse.");

            var orderIds = await dbContext.RouteOrders
                .Where(ro => ro.RouteId == routeMaster.Id)
                .Select(ro => ro.OrderId)
                .ToListAsync(ct);

            if (orderIds.Count == 0)
                throw new BusinessRuleException($"No orders found for RouteUuid {command.RouteUuid}.");

            var orders = await dbContext.WmsOrders
                .Include(o => o.WmsOrderItems)
                .Where(o => orderIds.Contains(o.Id))
                .AsNoTracking()
                .ToListAsync(ct);

            var ordersWithShortPick = new List<(WmsOrder Order, List<WmsOrderItem> ShortPickItems)>();

            foreach (var order in orders)
            {
                var shortPickItems = order.WmsOrderItems.Where(x =>
                (
                    x.PickedQuantity < x.TotalItemQuantity 
                    ||
                    (x.PickedQuantity == x.TotalItemQuantity  && x.ConfirmedQuantity < x.TotalItemQuantity)
                )                 
                && x.Status == "Done").ToList();

                if (shortPickItems.Count > 0)
                {
                    ordersWithShortPick.Add((order, shortPickItems));
                }
            }

            var allShortPickItems = ordersWithShortPick.SelectMany(x => x.ShortPickItems).ToList();

            await allocationService.AllocateShortfallAsync(warehouseId, allShortPickItems, ct);

            if (ordersWithShortPick.Count == 0)
            {
                logger.LogInformation(
                    "Short pick list generation skipped for route {RouteUuid} — no order items with short pick shortfall found.",
                    command.RouteUuid);

                throw new BusinessRuleException(
                    $"No short-picked order items found for route {command.RouteUuid}.");
            }

            routeMaster.IsShortItemRequestSent = true;
            routeMaster.UpdatedDate = DateTime.UtcNow;

            foreach (var (order, shortPickItems) in ordersWithShortPick)
            {
                foreach (var item in shortPickItems)
                {
                    item.ConfirmedQuantity = null;
                    item.Status = "PENDING";
                    item.UpdatedAt = DateTime.UtcNow;
                }
            }

            await dbContext.SaveChangesAsync(ct);

            foreach (var (order, shortPickItems) in ordersWithShortPick)
            {
                await generateShortPickListsQueue.EnqueueAsync(command.RouteUuid, order.Id, shortPickItems.Select(i => i.Id).ToList(), ct);

                logger.LogInformation(
                    "Short pick list generation queued for route {RouteUuid}, order {OrderUuid}, item count {ItemCount}.",
                    command.RouteUuid,
                    order.OrderUuid,
                    shortPickItems.Count);
            }

            return new ShortPickListGenerationMessageResponse(InProgressMessage);
        }
    }
}
