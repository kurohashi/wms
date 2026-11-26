using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.Work.Application.Common.Helpers;

namespace Wms.PickList.Application.Services.Cancellation;

public sealed class InProgress(
    WmsDbContextWrite dbContext,
    ILogger<InProgress> logger)
{
    public async Task ExecuteAsync(
        List<Wms.Domain.Entities.PickList> pickLists,
        List<long> cancelledOrderItemIds,
        List<Wms.Domain.Entities.PickListItem> cancelledPickListItems,
        CancellationToken ct)
    {
        if (pickLists.Count == 0 || cancelledOrderItemIds.Count == 0)
            return;

        var pickListIds = pickLists.Select(p => p.Id).ToList();

        logger.LogWarning(
            "Order cancellation requested for pick lists {PickListIds} currently IN_PROGRESS. Order items marked CANCELLED; picking will continue until drop, after which putaway will be created.",
            pickListIds);

        var utcNow = DateTime.UtcNow;

        // 1. Identify which picklists are IN_PROGRESS
        var inProgressPickLists = pickLists
            .Where(p => string.Equals(p.Status, nameof(PickListStatus.IN_PROGRESS), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var inProgressPickListIds = inProgressPickLists.Select(p => p.Id).ToHashSet();

        // 2. Identify order items actively in IN_PROGRESS picklists
        var inProgressOrderItemIds = cancelledPickListItems
            .Where(pi => inProgressPickListIds.Contains(pi.PickListId) && pi.OrderItemId.HasValue)
            .Select(pi => pi.OrderItemId!.Value)
            .ToHashSet();

        // 3. Unpicked items = Any order items NOT in an IN_PROGRESS picklist
        // (Includes items with NO picklist at all + items in PENDING/ASSIGNED picklists)
        var unpickedOrderItemIds = cancelledOrderItemIds
            .Where(id => !inProgressOrderItemIds.Contains(id))
            .Distinct()
            .ToList();

        // 4. Deallocate inventory allocations and bin reservations for unpicked items
        if (unpickedOrderItemIds.Count > 0)
        {
            var unpickedAllocations = await dbContext.InventoryAllocations
                .Where(ia => unpickedOrderItemIds.Contains(ia.OrderItemId))
                .ToListAsync(ct);

            var unpickedAllocationIds = unpickedAllocations.Select(ia => ia.Id).Distinct().ToList();

            var unpickedReservations = await dbContext.BinInventoryReservations
                .Where(br => unpickedAllocationIds.Contains(br.InventoryAllocationId)
                             || unpickedOrderItemIds.Contains(br.OrderItemId))
                .ToListAsync(ct);

            if (unpickedReservations.Count > 0)
            {
                dbContext.BinInventoryReservations.RemoveRange(unpickedReservations);
            }

            if (unpickedAllocations.Count > 0)
            {
                dbContext.InventoryAllocations.RemoveRange(unpickedAllocations);
            }
        }

        // 5. Cancel any PENDING or ASSIGNED picklists for this order
        var pendingOrAssignedPickLists = pickLists
            .Where(p => string.Equals(p.Status, nameof(PickListStatus.PENDING), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Status, nameof(PickListStatus.ASSIGNED), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pendingOrAssignedPickLists.Count > 0)
        {
            var pendingOrAssignedIds = pendingOrAssignedPickLists.Select(p => p.Id).ToHashSet();
            var pendingItems = cancelledPickListItems
                .Where(pi => pendingOrAssignedIds.Contains(pi.PickListId))
                .ToList();

            foreach (var pi in pendingItems)
            {
                pi.Status = nameof(PickListStatus.CANCELLED);
                pi.ModifiedDate = utcNow;
            }

            foreach (var pl in pendingOrAssignedPickLists)
            {
                var allItemsForPl = await dbContext.PickListItems
                    .Where(pi => pi.PickListId == pl.Id)
                    .ToListAsync(ct);

                var allCancelled = allItemsForPl.All(i =>
                    i.Status == nameof(PickListStatus.CANCELLED) ||
                    pendingItems.Select(p => p.Id).Contains(i.Id));

                if (allCancelled)
                {
                    pl.Status = nameof(PickListStatus.CANCELLED);
                    pl.ModifiedDate = utcNow;
                }
            }
        }

        var allRelevantOrderItemIds = cancelledOrderItemIds
            .Union(cancelledPickListItems.Where(pi => pi.OrderItemId.HasValue).Select(pi => pi.OrderItemId!.Value))
            .Distinct()
            .ToList();

        var orderItems = await dbContext.WmsOrderItems
            .Where(oi => allRelevantOrderItemIds.Contains(oi.Id))
            .ToListAsync(ct);

        foreach (var orderItem in orderItems)
        {
            orderItem.Status = nameof(WmsOrderStatusEnum.CANCELLED);
            orderItem.UpdatedAt = utcNow;
        }

        var orderIds = orderItems.Select(oi => oi.OrderId).Distinct().ToList();
        var orders = await dbContext.WmsOrders
            .Where(o => orderIds.Contains(o.Id))
            .ToListAsync(ct);

        foreach (var order in orders)
        {
            order.OrderStatus = nameof(WmsOrderStatusEnum.CANCELLED);
            order.UpdatedAt = utcNow;
            order.UpdatedDate = utcNow;
        }

        var orderItemById = orderItems.ToDictionary(oi => oi.Id);
        var orderById = orders.ToDictionary(o => o.Id);

        // For any picklists of this order that were already COMPLETED before cancellation arrived, mark them CANCELLED now
        var completedPickLists = pickLists
            .Where(p => string.Equals(p.Status, nameof(PickListStatus.COMPLETED), StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var cp in completedPickLists)
        {
            cp.Status = nameof(PickListStatus.CANCELLED);
            cp.ModifiedDate = utcNow;
        }

        var completedPickListIds = completedPickLists.Select(cp => cp.Id).ToList();
        var completedItems = cancelledPickListItems
            .Where(pi => completedPickListIds.Contains(pi.PickListId))
            .ToList();

        var itemsToDelete = new List<Wms.Domain.Entities.PickListItem>();

        foreach (var ci in completedItems)
        {
            var isStandard = false;
            int? orderQuantity = null;

            if (ci.OrderItemId.HasValue && orderItemById.TryGetValue(ci.OrderItemId.Value, out var orderItem))
            {
                orderQuantity = orderItem.Quantity;
                if (orderById.TryGetValue(orderItem.OrderId, out var order))
                {
                    isStandard = IsStandardDeliveryType(order.OrderDeliveryType);
                }
            }

            if (isStandard && orderQuantity.HasValue && ci.RequestedQty != orderQuantity.Value)
            {
                itemsToDelete.Add(ci);
            }
            else
            {
                ci.Status = nameof(PickListStatus.CANCELLED);
                ci.ModifiedDate = utcNow;
            }
        }

        if (itemsToDelete.Count > 0)
        {
            var itemsToDeleteIds = itemsToDelete.Select(i => i.Id).ToList();

            var relatedPickExecutions = await dbContext.PickExecutions
                .Where(pe => itemsToDeleteIds.Contains(pe.PickListItemId))
                .ToListAsync(ct);

            if (relatedPickExecutions.Count > 0)
            {
                dbContext.PickExecutions.RemoveRange(relatedPickExecutions);
            }

            var relatedAllocations = await dbContext.InventoryAllocations
                .Where(ia => ia.PickListItemId.HasValue && itemsToDeleteIds.Contains(ia.PickListItemId.Value))
                .ToListAsync(ct);

            foreach (var alloc in relatedAllocations)
            {
                alloc.PickListItemId = null;
            }

            dbContext.PickListItems.RemoveRange(itemsToDelete);
        }

        var cancelledPickListUuids = pickLists
            .Where(p => string.Equals(p.Status, nameof(PickListStatus.CANCELLED), StringComparison.OrdinalIgnoreCase))
            .Select(p => (Guid?)p.PickListUuid)
            .ToList();

        if (cancelledPickListUuids.Count > 0)
        {
            var tasks = await dbContext.Tasks
                .Include(t => t.Assignments)
                    .ThenInclude(a => a.Worker)
                .Include(t => t.Assignments)
                    .ThenInclude(a => a.WorkBatch)
                .Where(t => t.ReferenceId.HasValue
                            && cancelledPickListUuids.Contains(t.ReferenceId.Value)
                            && t.Status != nameof(TaskStatusEnum.COMPLETED)
                            && t.IsActive
                            && !t.IsDeleted)
                .ToListAsync(ct);

            var affectedWorkerUuids = new List<Guid>();

            foreach (var task in tasks)
            {
                task.Status = nameof(TaskStatusEnum.COMPLETED);
                task.ModifiedDate = utcNow;
                task.ModifiedBy = "SYSTEM_CANCELLATION";

                var activeAssignments = task.Assignments
                    .Where(a => a.Status != nameof(AssignmentStatusEnum.COMPLETED) && a.IsActive && !a.IsDeleted)
                    .ToList();

                foreach (var assignment in activeAssignments)
                {
                    assignment.Status = nameof(AssignmentStatusEnum.COMPLETED);
                    assignment.CompletedAt = utcNow;
                    assignment.ModifiedDate = utcNow;
                    assignment.ModifiedBy = "SYSTEM_CANCELLATION";

                    var start = assignment.StartedAt ?? assignment.AssignedAt;
                    if (start != default)
                    {
                        assignment.ActualSeconds = (decimal)(utcNow - start).TotalSeconds;
                    }

                    if (assignment.Worker != null)
                    {
                        affectedWorkerUuids.Add(assignment.Worker.WorkerUuid);
                    }

                    if (assignment.WorkBatch?.WorkerUuidList != null)
                    {
                        var batchUuids = assignment.WorkBatch.WorkerUuidList.Select(Guid.Parse);
                        affectedWorkerUuids.AddRange(batchUuids);
                    }
                }
            }

            if (affectedWorkerUuids.Count > 0)
            {
                await dbContext.SyncWorkersAvailabilityAsync(affectedWorkerUuids.Distinct(), "SYSTEM_CANCELLATION", utcNow, ct);
            }
        }

        await dbContext.SaveChangesAsync(ct);
    }

    private static bool IsStandardDeliveryType(string? deliveryType)
    {
        return string.Equals(deliveryType, nameof(DeliveryTypeEnum.STANDARD), StringComparison.OrdinalIgnoreCase)
            || string.Equals(deliveryType, "STANDARAD", StringComparison.OrdinalIgnoreCase);
    }
}