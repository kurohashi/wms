using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.Work.Application.Common.Helpers;

namespace Wms.PickList.Application.Command;

public sealed class CancelPickListCommand(string PickListNumber) : ICommand<bool>
{
    public string PickListNumber { get; init; } = PickListNumber ?? string.Empty;

    public sealed class Handler(
        WmsDbContextWrite dbContext,
        ICurrentUser currentUser) : ICommandHandler<CancelPickListCommand, bool>
    {
        public async Task<bool> HandleAsync(CancelPickListCommand command, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(command.PickListNumber))
                throw new BusinessRuleException("Pick list number or UUID is required.");

            var distinctIdentifiers = command.PickListNumber
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctIdentifiers.Count == 0)
                throw new BusinessRuleException("Pick list number or UUID is required.");

            var warehouseId = currentUser.WarehouseId
                ?? throw new BusinessRuleException("User is not associated with a warehouse.");

            var guidMap = new Dictionary<Guid, string>();
            var stringSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in distinctIdentifiers)
            {
                if (Guid.TryParse(id, out var guid))
                {
                    guidMap[guid] = id;
                }
                stringSet.Add(id);
            }
            var guidList = guidMap.Keys.ToList();

            var pickLists = await dbContext.PickLists
                .Where(pl => pl.WarehouseId == warehouseId &&
                    ((pl.PickListNumber != null && stringSet.Contains(pl.PickListNumber)) ||
                     (guidList.Count > 0 && guidList.Contains(pl.PickListUuid))))
                .ToListAsync(ct);

            var foundIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pl in pickLists)
            {
                if (!string.IsNullOrWhiteSpace(pl.PickListNumber) && stringSet.Contains(pl.PickListNumber))
                {
                    foundIdentifiers.Add(pl.PickListNumber);
                }
                if (guidMap.TryGetValue(pl.PickListUuid, out var originalGuidStr))
                {
                    foundIdentifiers.Add(originalGuidStr);
                }
            }

            var notFoundIdentifiers = distinctIdentifiers.Where(id => !foundIdentifiers.Contains(id)).ToList();
            if (notFoundIdentifiers.Count > 0)
                throw new BusinessRuleException($"Pick list with identifier '{string.Join(", ", notFoundIdentifiers)}' not found.");

            var inProgressOrCompleted = pickLists.Where(pl =>
                string.Equals(pl.Status, nameof(PickListStatus.IN_PROGRESS), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pl.Status, nameof(PickListStatus.COMPLETED), StringComparison.OrdinalIgnoreCase)).ToList();

            if (inProgressOrCompleted.Count > 0)
            {
                var numbers = string.Join(", ", inProgressOrCompleted.Select(p => !string.IsNullOrWhiteSpace(p.PickListNumber) ? p.PickListNumber : p.PickListUuid.ToString()));
                throw new BusinessRuleException($"Pick list '{numbers}' is not PENDING and cannot be cancelled.");
            }

            var alreadyCancelled = pickLists.Where(pl =>
                string.Equals(pl.Status, nameof(PickListStatus.CANCELLED), StringComparison.OrdinalIgnoreCase)).ToList();

            if (alreadyCancelled.Count > 0)
            {
                var numbers = string.Join(", ", alreadyCancelled.Select(p => !string.IsNullOrWhiteSpace(p.PickListNumber) ? p.PickListNumber : p.PickListUuid.ToString()));
                throw new BusinessRuleException($"Pick list '{numbers}' is already cancelled.");
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            try
            {
                var utcNow = DateTime.UtcNow;
                var userId = currentUser.UserId ?? "SYSTEM";
                var pickListIds = pickLists.Select(pl => pl.Id).Distinct().ToList();

                // 1. Fetch all items belonging to these pick lists
                var pickListItems = await dbContext.PickListItems
                    .Where(pli => pickListIds.Contains(pli.PickListId))
                    .ToListAsync(ct);

                var pickListItemIds = pickListItems.Select(pli => pli.Id).ToList();

                // 2. Remove any partial executions or reversals to prevent foreign key errors
                if (pickListItemIds.Count > 0)
                {
                    var relatedPickExecutions = await dbContext.PickExecutions
                        .Where(pe => pickListItemIds.Contains(pe.PickListItemId))
                        .ToListAsync(ct);

                    if (relatedPickExecutions.Count > 0)
                    {
                        var executionIds = relatedPickExecutions.Select(pe => pe.Id).ToList();
                        var reversals = await dbContext.PickReversals
                            .Where(pr => executionIds.Contains(pr.PickExecutionId))
                            .ToListAsync(ct);

                        if (reversals.Count > 0)
                        {
                            dbContext.PickReversals.RemoveRange(reversals);
                        }

                        dbContext.PickExecutions.RemoveRange(relatedPickExecutions);
                    }

                    // 3. Remove InventoryAllocations and BinInventoryReservations specifically belonging to these pick lists
                    var inventoryAllocations = await dbContext.InventoryAllocations
                        .Where(ia => ia.PickListItemId.HasValue && pickListItemIds.Contains(ia.PickListItemId.Value))
                        .ToListAsync(ct);

                    if (inventoryAllocations.Count > 0)
                    {
                        var allocationIds = inventoryAllocations.Select(ia => ia.Id).ToList();
                        var binReservations = await dbContext.BinInventoryReservations
                            .Where(bir => allocationIds.Contains(bir.InventoryAllocationId))
                            .ToListAsync(ct);

                        if (binReservations.Count > 0)
                        {
                            dbContext.BinInventoryReservations.RemoveRange(binReservations);
                        }

                        dbContext.InventoryAllocations.RemoveRange(inventoryAllocations);
                    }
                }

                // 4. Mark PickLists and PickListItems as CANCELLED
                foreach (var pickList in pickLists)
                {
                    pickList.Status = nameof(PickListStatus.CANCELLED);
                    pickList.ModifiedDate = utcNow;
                    pickList.ModifiedBy = userId;
                }

                foreach (var item in pickListItems)
                {
                    item.AllocatedQty = 0;
                    item.PickedQty = 0;
                    item.Status = nameof(PickListStatus.CANCELLED);
                    item.ModifiedDate = utcNow;
                    item.ModifiedBy = userId;
                }

                // 5. Update associated Order Items and parent Orders
                var relevantOrderItemIds = pickListItems
                    .Where(pli => pli.OrderItemId.HasValue)
                    .Select(pli => pli.OrderItemId!.Value)
                    .Distinct()
                    .ToList();

                if (relevantOrderItemIds.Count > 0)
                {
                    var orderItems = await dbContext.WmsOrderItems
                        .Where(oi => relevantOrderItemIds.Contains(oi.Id))
                        .ToListAsync(ct);

                    foreach (var item in orderItems)
                    {
                        item.PickedQuantity = 0;
                        item.ShortPickedQty = 0;
                        item.Status = nameof(WmsOrderStatusEnum.PENDING);
                        item.UpdatedAt = utcNow;
                        item.ModifiedBy = userId;
                    }

                    var orderIds = orderItems.Select(oi => oi.OrderId).Distinct().ToList();
                    var orders = await dbContext.WmsOrders
                        .Where(o => orderIds.Contains(o.Id))
                        .ToListAsync(ct);

                    // Check if other non-cancelled pick list items still exist for these order items
                    var otherActiveOrderItemIds = await dbContext.PickListItems
                        .Where(pli => !pickListIds.Contains(pli.PickListId)
                                      && pli.OrderItemId.HasValue
                                      && relevantOrderItemIds.Contains(pli.OrderItemId.Value)
                                      && pli.Status != nameof(PickListStatus.CANCELLED))
                        .Select(pli => pli.OrderItemId!.Value)
                        .Distinct()
                        .ToListAsync(ct);

                    foreach (var order in orders)
                    {
                        var hasOtherActiveItems = orderItems
                            .Where(oi => oi.OrderId == order.Id)
                            .Any(oi => otherActiveOrderItemIds.Contains(oi.Id));

                        if (!hasOtherActiveItems)
                        {
                            order.OrderStatus = nameof(WmsOrderStatusEnum.PENDING);
                            order.IsPicklistCreated = false;
                        }

                        order.UpdatedAt = utcNow;
                        order.UpdatedDate = utcNow;
                        order.ModifiedBy = userId;
                    }
                }

                // 6. Complete / Cancel associated active Tasks and Worker Assignments
                var pickListUuids = pickLists.Select(pl => pl.PickListUuid).Distinct().ToList();
                var tasks = await dbContext.Tasks
                    .Include(t => t.Assignments)
                        .ThenInclude(a => a.Worker)
                    .Include(t => t.Assignments)
                        .ThenInclude(a => a.WorkBatch)
                    .Where(t => t.ReferenceId.HasValue
                                && pickListUuids.Contains(t.ReferenceId.Value)
                                && t.Status != nameof(TaskStatusEnum.COMPLETED)
                                && t.IsActive
                                && !t.IsDeleted)
                    .ToListAsync(ct);

                var affectedWorkerUuids = new List<Guid>();
                foreach (var task in tasks)
                {
                    task.Status = nameof(TaskStatusEnum.COMPLETED);
                    task.ModifiedDate = utcNow;
                    task.ModifiedBy = userId;

                    var activeAssignments = task.Assignments
                        .Where(a => a.Status != nameof(AssignmentStatusEnum.COMPLETED) && a.IsActive && !a.IsDeleted)
                        .ToList();

                    foreach (var assignment in activeAssignments)
                    {
                        assignment.Status = nameof(AssignmentStatusEnum.COMPLETED);
                        assignment.CompletedAt = utcNow;
                        assignment.ModifiedDate = utcNow;
                        assignment.ModifiedBy = userId;

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
                    await dbContext.SyncWorkersAvailabilityAsync(affectedWorkerUuids.Distinct(), userId, utcNow, ct);
                }

                // 7. Persist changes and commit transaction
                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return true;
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }
    }
}