using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.Work.Application.Common.Helpers;

namespace Wms.PickList.Application.Services.Cancellation;

public sealed class Completed(
    WmsDbContextWrite dbContext,
    ILogger<Completed> logger)
{
    public async System.Threading.Tasks.Task ExecuteAsync(
        List<Wms.Domain.Entities.PickList> pickLists,
        List<long> cancelledOrderItemIds,
        List<Wms.Domain.Entities.PickListItem> cancelledPickListItems,
        CancellationToken ct)
    {
        if (pickLists.Count == 0 || cancelledOrderItemIds.Count == 0)
            return;

        var pickListIds = pickLists.Select(p => p.Id).ToList();

        logger.LogWarning(
            "Order cancellation requested for pick lists {PickListIds} where ALL pick lists are already COMPLETED. Creating replenishment tasks for dropped inventory.",
            pickListIds);

        var utcNow = DateTime.UtcNow;

        // 1. Order items that were ACTUALLY in the completed picklists
        var completedOrderItemIds = cancelledPickListItems
            .Where(pi => pi.OrderItemId.HasValue)
            .Select(pi => pi.OrderItemId!.Value)
            .ToHashSet();

        // 2. Unpicked order items = items of this order that NEVER had a picklist generated
        var unpickedOrderItemIds = cancelledOrderItemIds
            .Where(id => !completedOrderItemIds.Contains(id))
            .Distinct()
            .ToList();

        // 3. Deallocate inventory allocations and bin reservations for unpicked items
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

        var itemsToDelete = new List<Wms.Domain.Entities.PickListItem>();

        foreach (var item in cancelledPickListItems)
        {
            var isStandard = false;
            int? orderQuantity = null;

            if (item.OrderItemId.HasValue && orderItemById.TryGetValue(item.OrderItemId.Value, out var orderItem))
            {
                orderQuantity = orderItem.Quantity;
                if (orderById.TryGetValue(orderItem.OrderId, out var order))
                {
                    isStandard = IsStandardDeliveryType(order.OrderDeliveryType);
                }
            }

            if (isStandard && orderQuantity.HasValue && item.RequestedQty != orderQuantity.Value)
            {
                itemsToDelete.Add(item);
            }
            else
            {
                item.Status = nameof(PickListStatus.CANCELLED);
                item.ModifiedDate = utcNow;
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

        foreach (var picklist in pickLists)
        {
            picklist.Status = nameof(PickListStatus.CANCELLED);
            picklist.ModifiedDate = utcNow;
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

        // For each cancelled order, create replenishment task from drop allocations ONLY for completed items
        foreach (var order in orders)
        {
            var orderIdentifier = order.UniqueOrderId ?? order.Id.ToString();

            var taskAlreadyExists = await dbContext.Tasks
                .AnyAsync(t => t.WarehouseId == order.WarehouseId
                             && t.Type == nameof(TaskTypeEnum.REPLENISHMENT)
                             && (t.OrderRef == orderIdentifier || (t.TaskName != null && t.TaskName.Contains(orderIdentifier))), ct);

            if (taskAlreadyExists)
                continue;

            var allOrderItems = await dbContext.WmsOrderItems
                .Where(oi => oi.OrderId == order.Id)
                .ToListAsync(ct);

            var skuByOrderItemId = allOrderItems.ToDictionary(oi => oi.Id, oi => oi.Sku);

            var dropAllocations = await dbContext.InventoryAllocations
                .Where(ia => completedOrderItemIds.Contains(ia.OrderItemId)
                             && ia.FromBin.HasValue
                             && ia.IsActive == true
                             && ia.IsDeleted != true
                             && ia.AllocatedQty > 0
                             && ia.InventoryStockId == 0)
                .ToListAsync(ct);

            var movementRequests = dropAllocations
                .Where(da => skuByOrderItemId.ContainsKey(da.OrderItemId) && da.BatchId.HasValue && da.FromBin.HasValue && da.AllocatedQty > 0)
                .Select(da => new BatchMovementItemRequest(
                    SkuId: skuByOrderItemId[da.OrderItemId],
                    BatchId: da.BatchId!.Value,
                    FromLocationId: da.FromBin!.Value,
                    Quantity: da.AllocatedQty))
                .ToList();

            if (movementRequests.Count > 0)
            {
                await dbContext.CreateBatchMovementAndTaskAsync(
                    warehouseId: order.WarehouseId,
                    movementType: "REPLENISHMENT",
                    taskName: $"Replenishment Task for Cancelled Order {orderIdentifier}",
                    referenceName: "BatchMovement",
                    items: movementRequests,
                    userId: "SYSTEM_CANCELLATION",
                    utcNow: utcNow,
                    orderRef: orderIdentifier,
                    ct: ct);
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