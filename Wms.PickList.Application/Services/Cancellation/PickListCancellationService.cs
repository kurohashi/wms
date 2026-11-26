using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;

namespace Wms.PickList.Application.Services.Cancellation;

public sealed class PickListCancellationService(
    WmsDbContextWrite dbContext,
    PendingOrAssigned pendingOrAssigned,
    InProgress inProgress,
    Completed completed,
    SyncInventory syncInventory,
    ILogger<PickListCancellationService> logger) : IPickListCancellationService
{
    private static int GetCancellationCase(List<Wms.Domain.Entities.PickList> pickLists)
    {
        bool hasInProgress = pickLists.Any(p => string.Equals(p.Status, nameof(PickListStatus.IN_PROGRESS), StringComparison.OrdinalIgnoreCase));
        int hasCompleted = pickLists.Count(p => string.Equals(p.Status, nameof(PickListStatus.COMPLETED), StringComparison.OrdinalIgnoreCase));

        if (hasCompleted == pickLists.Count) return 2;
        if (hasInProgress || (hasCompleted > 0)) return 1;
        return 0;
    }

    public async Task CancelPickListsAsync(Guid orderUuid, CancellationToken ct)
    {
        var wmsOrder = await dbContext.WmsOrders
            .FirstOrDefaultAsync(w => w.OrderUuid == orderUuid, ct);

        if (wmsOrder == null)
        {
            logger.LogWarning("Order with UUID {OrderUuid} not found. Skipping pick list cancellation.", orderUuid);
            return;
        }

        var wmsOrderItems = await dbContext.WmsOrderItems
            .Where(wi => wi.OrderId == wmsOrder.Id)
            .Distinct()
            .ToListAsync(ct);

        if (wmsOrderItems.Count == 0)
        {
            logger.LogInformation("No order items found for order with UUID {OrderUuid}. Nothing to cancel.", orderUuid);
            return;
        }

        var orderItemIds = wmsOrderItems.Select(wi => wi.Id).Distinct().ToList();

        var pickListItems = await dbContext.PickListItems
            .Where(pi => pi.OrderItemId.HasValue && orderItemIds.Contains(pi.OrderItemId.Value))
            .Distinct()
            .ToListAsync(ct);

        if (pickListItems.Count == 0)
        {
            logger.LogInformation("No pick list items found for order with UUID {OrderUuid}. Deallocating order inventory allocations.", orderUuid);
            await DeallocateOrderInventoryAsync(orderItemIds, ct);
            return;
        }

        var pickListIds = pickListItems.Select(pi => pi.PickListId).Distinct().ToList();

        var pickLists = await dbContext.PickLists
            .Where(p => pickListIds.Contains(p.Id))
            .Distinct()
            .ToListAsync(ct);

        if (pickLists.Count == 0)
        {
            logger.LogInformation("No pick lists found for order with UUID {OrderUuid}. Deallocating order inventory allocations.", orderUuid);
            await DeallocateOrderInventoryAsync(orderItemIds, ct);
            return;
        }

        await ProcessPickList(pickLists, orderItemIds, pickListItems, ct);
    }

    private async Task DeallocateOrderInventoryAsync(List<long> orderItemIds, CancellationToken ct)
    {
        var allocations = await dbContext.InventoryAllocations
            .Where(ia => orderItemIds.Contains(ia.OrderItemId))
            .ToListAsync(ct);

        var allocationIds = allocations.Select(ia => ia.Id).Distinct().ToList();
        var binReservations = await dbContext.BinInventoryReservations
            .Where(br => allocationIds.Contains(br.InventoryAllocationId)
                         || orderItemIds.Contains(br.OrderItemId))
            .ToListAsync(ct);

        if (binReservations.Count > 0)
        {
            dbContext.BinInventoryReservations.RemoveRange(binReservations);
        }

        if (allocations.Count > 0)
        {
            dbContext.InventoryAllocations.RemoveRange(allocations);
        }

        await dbContext.SaveChangesAsync(ct);

        await syncInventory.ExecuteAsync(orderItemIds, ct);
    }

    private async Task ProcessPickList(List<Wms.Domain.Entities.PickList> pickLists, List<long> cancelledOrderItemIds, List<Wms.Domain.Entities.PickListItem> cancelledPickListItems, CancellationToken ct)
    {   
        int cancellationCase = GetCancellationCase(pickLists);

        switch (cancellationCase)
        {
            case 0:
                await pendingOrAssigned.ExecuteAsync(pickLists, cancelledOrderItemIds, cancelledPickListItems, ct);
                break;
            case 1:
                await inProgress.ExecuteAsync(pickLists, cancelledOrderItemIds, cancelledPickListItems, ct);
                break;
            case 2:
                await completed.ExecuteAsync(pickLists, cancelledOrderItemIds, cancelledPickListItems, ct);
                break;
            default:
                logger.LogWarning("Unknown cancellation case {CancellationCase} for pick lists. No action taken.", cancellationCase);
                break;
        }
    }
}