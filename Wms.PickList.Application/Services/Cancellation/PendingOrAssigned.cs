using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.Work.Application.Common.Helpers;

namespace Wms.PickList.Application.Services.Cancellation;

public sealed class PendingOrAssigned(WmsDbContextWrite dbContext, ILogger<PendingOrAssigned> logger)
{
    public async Task ExecuteAsync(
        List<Wms.Domain.Entities.PickList> pickLists,
        List<long> cancelledOrderItemIds,
        List<Wms.Domain.Entities.PickListItem> cancelledPickListItems,
        CancellationToken ct)
    {
        if (pickLists.Count == 0 || cancelledPickListItems.Count == 0)
            return;

        var hasExistingTransaction = dbContext.Database.CurrentTransaction != null;
        var transaction = hasExistingTransaction ? null : await dbContext.Database.BeginTransactionAsync(ct);

        try
        {
            var pickListIds = pickLists.Select(p => p.Id).Distinct().ToList();
            var pickListItemIds = cancelledPickListItems.Select(pi => pi.Id).Distinct().ToList();

            var inventoryAllocations = await dbContext.InventoryAllocations
                .Where(ia => (ia.PickListItemId.HasValue && pickListItemIds.Contains(ia.PickListItemId.Value)) || cancelledOrderItemIds.Contains(ia.OrderItemId))
                .ToListAsync(ct);

            var allocationIds = inventoryAllocations.Select(ia => ia.Id).Distinct().ToList();
            var binReservations = await dbContext.BinInventoryReservations
                .Where(br => allocationIds.Contains(br.InventoryAllocationId) || cancelledOrderItemIds.Contains(br.OrderItemId))
                .ToListAsync(ct);

            if (binReservations.Count > 0)
            {
                dbContext.BinInventoryReservations.RemoveRange(binReservations);
            }

            if (inventoryAllocations.Count > 0)
            {
                dbContext.InventoryAllocations.RemoveRange(inventoryAllocations);
            }

            var allRelevantOrderItemIds = cancelledOrderItemIds
                .Union(cancelledPickListItems.Where(pi => pi.OrderItemId.HasValue).Select(pi => pi.OrderItemId!.Value))
                .Distinct()
                .ToList();

            var orderItems = await dbContext.WmsOrderItems
                .Where(oi => allRelevantOrderItemIds.Contains(oi.Id))
                .ToListAsync(ct);

            var orderItemById = orderItems.ToDictionary(oi => oi.Id);
            var orderIds = orderItems.Select(oi => oi.OrderId).Distinct().ToList();
            var orders = await dbContext.WmsOrders
                .Where(o => orderIds.Contains(o.Id))
                .ToListAsync(ct);
            var orderById = orders.ToDictionary(o => o.Id);

            var utcNow = DateTime.UtcNow;
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

                dbContext.PickListItems.RemoveRange(itemsToDelete);
            }

            foreach (var picklist in pickLists)
            {
                var allItems = await dbContext.PickListItems
                    .Where(pi => pi.PickListId == picklist.Id)
                    .ToListAsync(ct);

                var allCancelled = allItems.All(i =>
                    pickListItemIds.Contains(i.Id) || i.Status == nameof(PickListStatus.CANCELLED));

                if (allCancelled)
                {
                    picklist.Status = nameof(PickListStatus.CANCELLED);
                    picklist.ModifiedDate = utcNow;
                }
            }

            foreach (var orderItem in orderItems)
            {
                orderItem.Status = nameof(WmsOrderStatusEnum.CANCELLED);
                orderItem.UpdatedAt = utcNow;
            }

            foreach (var order in orders)
            {
                order.OrderStatus = nameof(WmsOrderStatusEnum.CANCELLED);
                order.UpdatedAt = utcNow;
                order.UpdatedDate = utcNow;
            }

            var cancelledPickListUuids = pickLists
                .Where(p => p.Status == nameof(PickListStatus.CANCELLED))
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

            if (transaction != null)
            {
                await transaction.CommitAsync(ct);
            }

            logger.LogInformation(
                "Successfully cancelled pick list items {PickListItemIds} for pick lists {PickListIds}.",
                pickListItemIds,
                pickListIds);
        }
        catch (Exception ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(ct);
            }
            logger.LogError(ex, "Failed to cancel pending/assigned pick lists for pick lists {PickListIds}.", pickLists.Select(p => p.Id));
            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static bool IsStandardDeliveryType(string? deliveryType)
    {
        return string.Equals(deliveryType, nameof(DeliveryTypeEnum.STANDARD), StringComparison.OrdinalIgnoreCase)
            || string.Equals(deliveryType, "STANDARAD", StringComparison.OrdinalIgnoreCase);
    }
}
