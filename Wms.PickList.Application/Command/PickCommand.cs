using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Command;

public sealed class PickCommand(NextPickingRequestDto dto) : ICommand<NextPickingResponseDto>
{
    public Guid PickListItemUuid { get; } = dto.PickListItemUuid;
    public Guid WorkerUuid { get; } = dto.WorkerUuid;
    public List<BinPickDto> Bins { get; } = dto.Bins;

    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser) : ICommandHandler<PickCommand, NextPickingResponseDto>
    {
        public async Task<NextPickingResponseDto> HandleAsync(PickCommand command, CancellationToken ct = default)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            try
            {
                var utcNow = DateTime.UtcNow;

                long warehouseId = currentUser.WarehouseId ?? 0;
                if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

                if (command.Bins == null || command.Bins.Count == 0)
                    throw new BusinessRuleException("At least one bin pick details is required.");

                foreach (var binPick in command.Bins)
                {
                    if (binPick.PickQty < 0)
                        throw new BusinessRuleException("Pick Quantity cannot be negative.");
                }

                var targetPickListItem = await dbContext.PickListItems
                    .Include(pi => pi.PickList)
                    .FirstOrDefaultAsync(pi => pi.PickListItemUuid == command.PickListItemUuid, ct) ??
                    throw new BusinessRuleException("Picklist Item not found");

                var worker = await dbContext.Workers
                    .FirstOrDefaultAsync(x => x.WorkerUuid == command.WorkerUuid
                    && x.WarehouseId == warehouseId && x.IsActive && !x.IsDeleted, ct) ??
                   throw new BusinessRuleException("Worker not found.");

                // Fetch ALL PickListItems for the same PickList and same SKU
                var skuPickListItems = await dbContext.PickListItems
                    .Where(pi => pi.PickListId == targetPickListItem.PickListId
                              && pi.SkuId == targetPickListItem.SkuId
                              && pi.IsActive == true
                              && pi.IsDeleted == false)
                    .OrderBy(pi => pi.Id)
                    .ToListAsync(ct);

                var pickListItemIds = skuPickListItems.Select(pi => pi.Id).ToList();

                // Fetch ALL InventoryAllocations for these PickListItems
                var skuAllocations = await dbContext.InventoryAllocations
                    .Include(ia => ia.Batch)
                    .Where(ia => ia.PickListItemId != null
                              && pickListItemIds.Contains(ia.PickListItemId.Value)
                              && ia.IsActive == true
                              && ia.IsDeleted == false)
                    .OrderBy(ia => ia.PickListItemId)
                    .ThenBy(ia => ia.Sequence ?? ia.Id)
                    .ToListAsync(ct);

                if (!skuAllocations.Any())
                    throw new BusinessRuleException("Inventory Allocations not found");

                var totalAllocatedQty = skuPickListItems.Sum(pi => (int)(pi.AllocatedQty ?? pi.RequestedQty));
                var totalPickedQtyFromBins = command.Bins.Sum(b => b.PickQty);

                if (totalPickedQtyFromBins > totalAllocatedQty)
                    throw new BusinessRuleException($"Picked Quantity ({totalPickedQtyFromBins}) cannot be greater than Required Quantity ({totalAllocatedQty})");

                var binUuids = command.Bins.Select(b => b.BinUuid).ToList();
                var bins = await dbContext.Bins.Where(b => binUuids.Contains(b.BinUuid)).ToListAsync(ct);

                // Fetch all allocations for all items in the picklist to validate if any bin for any SKU is locked
                var allPickListAllocations = await dbContext.InventoryAllocations
                    .Where(ia => ia.PickListItem != null
                              && ia.PickListItem.PickListId == targetPickListItem.PickListId
                              && ia.IsActive == true
                              && ia.IsDeleted == false)
                    .Select(ia => new { ia.FromBin, SkuId = ia.PickListItem!.SkuId })
                    .ToListAsync(ct);

                var allocatedBinIds = allPickListAllocations
                    .Where(a => a.FromBin.HasValue)
                    .Select(a => a.FromBin!.Value)
                    .Distinct()
                    .ToList();

                var allBins = await dbContext.Bins
                    .Where(b => binUuids.Contains(b.BinUuid) || allocatedBinIds.Contains(b.Id))
                    .ToListAsync(ct);

                var binsById = allBins.ToDictionary(b => b.Id);

                var allPicklistSkuIds = allPickListAllocations
                    .Select(a => a.SkuId)
                    .Append(targetPickListItem.SkuId)
                    .Distinct()
                    .ToList();

                var products = await dbContext.Products
                    .AsNoTracking()
                    .Where(p => allPicklistSkuIds.Contains(p.Sku) || allPicklistSkuIds.Contains(p.Id))
                    .ToListAsync(ct);

                string GetSkuDisplay(long skuId)
                {
                    var prod = products.FirstOrDefault(p => p.Sku == skuId || p.Id == skuId);
                    if (!string.IsNullOrWhiteSpace(prod?.ProductCode))
                    {
                        return prod.ProductCode.StartsWith("SKU", StringComparison.OrdinalIgnoreCase)
                            ? prod.ProductCode
                            : $"SKU{prod.ProductCode}";
                    }
                    return $"SKU{skuId}";
                }

                var lockedBinErrors = allPickListAllocations
                    .Where(a => a.FromBin.HasValue
                             && binsById.TryGetValue(a.FromBin.Value, out var b)
                             && b.IsLocked)
                    .Select(a =>
                    {
                        var bin = binsById[a.FromBin!.Value];
                        var skuDisplay = GetSkuDisplay(a.SkuId);
                        var binCode = !string.IsNullOrWhiteSpace(bin.Code) ? bin.Code : bin.Id.ToString();
                        return (BinCode: binCode, SkuDisplay: skuDisplay);
                    })
                    .ToList();

                // Also check if any manually selected bin in command.Bins is locked
                foreach (var b in bins.Where(b => b.IsLocked))
                {
                    var skuDisplay = GetSkuDisplay(targetPickListItem.SkuId);
                    var binCode = !string.IsNullOrWhiteSpace(b.Code) ? b.Code : b.Id.ToString();
                    lockedBinErrors.Add((BinCode: binCode, SkuDisplay: skuDisplay));
                }

                var distinctLockedErrors = lockedBinErrors
                    .Distinct()
                    .Select(x => $"Bin {x.BinCode} is locked for {x.SkuDisplay}")
                    .ToList();

                if (distinctLockedErrors.Count > 0)
                {
                    var errorMessage = distinctLockedErrors.Count == 1
                        ? $"cannot complete picklist as {distinctLockedErrors[0]}"
                        : $"cannot complete picklist as {string.Join(", ", distinctLockedErrors)}";

                    throw new BusinessRuleException(errorMessage);
                }

                var skuIdsArray = new long[] { targetPickListItem.SkuId };
                var binStockDetails = await dbContext.Database
                    .SqlQuery<BinAvailableStockDetailDto>($"SELECT * FROM get_bin_available_stock_by_sku_list({skuIdsArray}, {warehouseId}, NULL::bigint[], false)")
                    .ToListAsync(ct);

                var damageDetails = await dbContext.PutawayRejections
                    .Where(pr => pr.PickListItemId.HasValue && pickListItemIds.Contains(pr.PickListItemId.Value) && skuIdsArray.Contains(pr.SkuId ?? 0))
                    .GroupBy(pr => new { pr.SkuId, pr.Location.BinUuid })
                    .Select(g => new
                    {
                        SkuId = g.Key.SkuId,
                        BinUuid = g.Key.BinUuid,
                        TotalDamageQty = (long)g.Sum(pr => pr.Quantity)
                    })
                    .ToListAsync(ct);

                var remainingPickedQtyByBin = new Dictionary<long, int>();
                foreach (var binPick in command.Bins)
                {
                    var bin = bins.FirstOrDefault(b => b.BinUuid == binPick.BinUuid)
                        ?? throw new BusinessRuleException("Bin not found");

                    if (remainingPickedQtyByBin.ContainsKey(bin.Id))
                        throw new BusinessRuleException($"Duplicate bin entry found for Bin {bin.Code}.");

                    remainingPickedQtyByBin[bin.Id] = binPick.PickQty;

                    // Only validate non-cluster, non-routable bins 
                    if (!bin.IsClusterBased && !bin.IsRoutable && binPick.PickQty > 0)
                    {
                        var stockDetail = binStockDetails.FirstOrDefault(b => b.BinUuid == bin.BinUuid && b.SkuId == targetPickListItem.SkuId && b.BatchId == binPick.LocationInventoryBatchId);
                        if (stockDetail != null)
                        {
                            if (binPick.PickQty > stockDetail.StockOnHand)
                                throw new BusinessRuleException($"Picked Quantity ({binPick.PickQty}) cannot be greater than Stock On Hand ({stockDetail.StockOnHand}) for Bin {bin.Code}");
                        }
                        else
                        {
                            throw new BusinessRuleException($"No stock available for SKU {targetPickListItem.SkuId} in Bin {bin.Code}");
                        }
                    }
                    // check for cluster/routing bin, (damageqty??0) + pickqty)== totalAllocatedQty
                    // if not then throw error
                    if ((bin.IsClusterBased || bin.IsRoutable) && !bin.IsPickable)
                    {
                        var damageDetail = damageDetails.FirstOrDefault(b => b.SkuId == targetPickListItem.SkuId && b.BinUuid == bin.BinUuid);
                        var damageQty = damageDetail?.TotalDamageQty ?? 0;
                        if ((damageQty + binPick.PickQty) != totalAllocatedQty)
                        {
                            throw new BusinessRuleException($"The sum of Picked Quantity ({binPick.PickQty}) and Damaged Quantity ({damageQty}) must be equal to Total Allocated Quantity ({totalAllocatedQty}) for Bin {bin.Code}.");
                        }
                    }
                }

                // Option A: Order Priority Waterfall Fulfillment
                // Order allocations by PickListItemId (Order Priority), FEFO batch expiry date, Sequence, Id
                var orderedAllocations = skuAllocations
                    .OrderBy(ia => ia.PickListItemId)
                    .ThenBy(ia => ia.Batch?.ExpiryDate ?? DateOnly.MaxValue)
                    .ThenBy(ia => ia.Sequence ?? ia.Id)
                    .ThenBy(ia => ia.Id)
                    .ToList();

                var pickExecutionlogs = new List<PickExecution>();

                // Track remaining quantity needed per PickListItem to prevent any single order item from consuming more than its allocated/requested qty
                var remainingNeededByItem = skuPickListItems.ToDictionary(
                    pi => pi.Id,
                    pi => (int)(pi.AllocatedQty ?? pi.RequestedQty)
                );

                var pickedQtyByAllocId = orderedAllocations.ToDictionary(a => a.Id, _ => 0);

                var orderItemIds = orderedAllocations.Select(a => a.OrderItemId).Distinct().ToList();
                var orderItems = await dbContext.WmsOrderItems
                    .Where(oi => orderItemIds.Contains(oi.Id))
                    .ToDictionaryAsync(oi => oi.Id, ct);

                var allocIds = orderedAllocations.Select(a => a.Id).ToList();
                var existingReservations = await dbContext.BinInventoryReservations
                    .Where(r => allocIds.Contains(r.InventoryAllocationId))
                    .ToListAsync(ct);

                // Phase 1: Primary Assigned Bin Consumption
                foreach (var alloc in orderedAllocations)
                {
                    var itemId = alloc.PickListItemId!.Value;
                    var itemStillNeeds = remainingNeededByItem[itemId];
                    if (itemStillNeeds <= 0) continue;

                    var allocNeeded = Math.Min(alloc.AllocatedQty, itemStillNeeds);

                    if (alloc.FromBin.HasValue
                        && remainingPickedQtyByBin.TryGetValue(alloc.FromBin.Value, out var primaryAvail)
                        && primaryAvail > 0)
                    {
                        var takeFromPrimary = Math.Min(primaryAvail, allocNeeded);
                        pickedQtyByAllocId[alloc.Id] += takeFromPrimary;
                        remainingPickedQtyByBin[alloc.FromBin.Value] -= takeFromPrimary;
                        remainingNeededByItem[itemId] -= takeFromPrimary;

                        pickExecutionlogs.Add(new PickExecution
                        {
                            PickListItemId = itemId,
                            PickedBy = worker.Id,
                            SourceBinId = alloc.FromBin.Value,
                            PickedQty = takeFromPrimary,
                            ShortQty = alloc.AllocatedQty - takeFromPrimary,
                            IsActive = true,
                            IsDeleted = false,
                            CreatedDate = utcNow
                        });

                        var reservation = existingReservations
                            .FirstOrDefault(r => r.InventoryAllocationId == alloc.Id && r.BinId == alloc.FromBin.Value);

                        if (reservation != null)
                        {
                            reservation.PickedQty = takeFromPrimary;
                            if (reservation.ReservedQty == 0 && takeFromPrimary > 0)
                                reservation.ReservedQty = takeFromPrimary;
                            reservation.Status = nameof(InventoryStatus.PICKED);
                            reservation.UpdatedAt = utcNow;
                        }
                    }
                    else if (alloc.FromBin.HasValue)
                    {
                        var reservation = existingReservations
                            .FirstOrDefault(r => r.InventoryAllocationId == alloc.Id && r.BinId == alloc.FromBin.Value);

                        if (reservation != null)
                        {
                            reservation.PickedQty = 0;
                            reservation.Status = nameof(InventoryStatus.PICKED);
                            reservation.UpdatedAt = utcNow;
                        }
                    }
                }

                // Phase 2: Fallback / Substitute Bins (Only for allocations that still need quantity)
                foreach (var alloc in orderedAllocations)
                {
                    var itemId = alloc.PickListItemId!.Value;
                    var itemStillNeeds = remainingNeededByItem[itemId];
                    if (itemStillNeeds <= 0) continue;

                    var allocAlreadyPicked = pickedQtyByAllocId[alloc.Id];
                    var allocNeeded = Math.Min(alloc.AllocatedQty - allocAlreadyPicked, itemStillNeeds);
                    if (allocNeeded <= 0) continue;

                    orderItems.TryGetValue(alloc.OrderItemId, out var orderItemForAlloc);

                    var availableBinIds = remainingPickedQtyByBin.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
                    foreach (var binId in availableBinIds)
                    {
                        if (allocNeeded == 0) break;

                        var otherAvail = remainingPickedQtyByBin[binId];
                        var takeFromOther = Math.Min(otherAvail, allocNeeded);

                        pickedQtyByAllocId[alloc.Id] += takeFromOther;
                        allocNeeded -= takeFromOther;
                        remainingPickedQtyByBin[binId] -= takeFromOther;
                        remainingNeededByItem[itemId] -= takeFromOther;

                        pickExecutionlogs.Add(new PickExecution
                        {
                            PickListItemId = itemId,
                            PickedBy = worker.Id,
                            SourceBinId = binId,
                            PickedQty = takeFromOther,
                            ShortQty = allocNeeded,
                            IsActive = true,
                            IsDeleted = false,
                            CreatedDate = utcNow
                        });

                        if (orderItemForAlloc != null && takeFromOther > 0)
                        {
                            var reservation = existingReservations
                                .FirstOrDefault(r => r.InventoryAllocationId == alloc.Id && r.BinId == binId);

                            if (reservation != null)
                            {
                                reservation.PickedQty = (reservation.PickedQty ?? 0) + takeFromOther;
                                if (reservation.ReservedQty == 0)
                                    reservation.ReservedQty = takeFromOther;
                                reservation.Status = nameof(InventoryStatus.PICKED);
                                reservation.UpdatedAt = utcNow;
                            }
                        }
                    }
                }

                // Finalize allocation statuses
                foreach (var alloc in orderedAllocations)
                {
                    var allocPicked = pickedQtyByAllocId[alloc.Id];
                    alloc.PickedQty = allocPicked;
                    alloc.Status = allocPicked == alloc.AllocatedQty
                        ? nameof(WorkerItemPickStatus.COMPLETED)
                        : nameof(WorkerItemPickStatus.SHORT_PICK);
                    alloc.ModifiedDate = utcNow;
                }

                // Finalize any untouched reservations (e.g. unpicked/skipped allocations)
                foreach (var reservation in existingReservations.Where(r => r.Status != nameof(InventoryStatus.PICKED)))
                {
                    reservation.PickedQty = 0;
                    reservation.Status = nameof(InventoryStatus.PICKED);
                    reservation.UpdatedAt = utcNow;
                }
                await dbContext.PickExecutions.AddRangeAsync(pickExecutionlogs, ct);
                await dbContext.SaveChangesAsync(ct);

                // Waterfall distribution across PickListItems and WmsOrderItems
                var affectedOrderIds = new HashSet<long>();

                foreach (var item in skuPickListItems)
                {
                    var itemAllocated = (int)(item.AllocatedQty ?? item.RequestedQty);
                    var itemPicked = skuAllocations
                        .Where(ia => ia.PickListItemId == item.Id)
                        .Sum(ia => ia.PickedQty ?? 0);

                    var itemShort = itemAllocated - itemPicked;
                    if (itemShort < 0) itemShort = 0;

                    item.PickedQty = itemPicked;
                    item.Status = itemShort == 0
                        ? nameof(WorkerItemPickStatus.COMPLETED)
                        : nameof(WorkerItemPickStatus.SHORT_PICK);
                    item.ModifiedDate = utcNow;

                    if (item.OrderItemId.HasValue)
                    {
                        var orderItemId = item.OrderItemId.Value;
                        var orderItem = await dbContext.WmsOrderItems
                            .FirstOrDefaultAsync(oi => oi.Id == orderItemId, ct);

                        if (orderItem != null)
                        {
                            var currentDropLocationId = targetPickListItem.PickList?.DropLocationId;

                            // Calculate total picked quantity across all sibling picklist items in the same leg / DropLocationId
                            var totalPickedInThisLeg = await dbContext.PickListItems
                                .Where(pi => pi.OrderItemId == orderItemId
                                          && pi.IsActive == true
                                          && pi.IsDeleted == false
                                          && pi.PickList.DropLocationId == currentDropLocationId)
                                .SumAsync(pi => pi.PickedQty ?? 0, ct);

                            // Check if any sibling picklists in the same leg are still pending
                            var hasPendingSiblings = await dbContext.PickListItems
                                .AnyAsync(pi => pi.OrderItemId == orderItemId
                                             && pi.IsActive == true
                                             && pi.IsDeleted == false
                                             && pi.PickList.DropLocationId == currentDropLocationId
                                             && pi.Status != nameof(WorkerItemPickStatus.COMPLETED)
                                             && pi.Status != nameof(WorkerItemPickStatus.SHORT_PICK), ct);

                            var totalPickedInt = (int)totalPickedInThisLeg;
                            var totalShort = Math.Max(0, orderItem.TotalItemQuantity - totalPickedInt);

                            orderItem.PickedQuantity = totalPickedInt;
                            orderItem.ShortPickedQty = totalShort;
                            orderItem.IsShortPick = totalShort > 0;

                            if (!hasPendingSiblings)
                            {
                                orderItem.Status = nameof(OrderItemStatusEnum.PICKING_COMPLETED);
                                affectedOrderIds.Add(orderItem.OrderId);
                            }
                            orderItem.UpdatedAt = utcNow;
                        }
                    }
                }

                // Update overall PickList status
                var pickList = await dbContext.PickLists
                    .Include(p => p.PickListItems)
                    .FirstOrDefaultAsync(p => p.Id == targetPickListItem.PickListId, ct);

                if (pickList != null)
                {
                    var activeItems = pickList.PickListItems
                        .Where(i => i.IsActive == true && i.IsDeleted != true)
                        .ToList();

                    var allItemsFinished = activeItems.All(i =>
                        i.Status == nameof(WorkerItemPickStatus.COMPLETED) ||
                        i.Status == nameof(WorkerItemPickStatus.SHORT_PICK));

                    if (allItemsFinished)
                    {
                        var hasShortPick = activeItems.Any(i => i.Status == nameof(WorkerItemPickStatus.SHORT_PICK));
                        pickList.Status = hasShortPick
                            ? nameof(PickListStatus.SHORT_PICK)
                            : nameof(PickListStatus.COMPLETED);
                    }
                    else
                    {
                        pickList.Status = nameof(PickListStatus.IN_PROGRESS);
                    }
                    pickList.ModifiedDate = utcNow;
                }

                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                if (affectedOrderIds.Count > 0 && OrderPickingCompletedQueueAccessor.Instance is { } pickingCompletedQueue)
                {
                    foreach (var orderId in affectedOrderIds)
                    {
                        await pickingCompletedQueue.EnqueueAsync(orderId, ct);
                    }
                }

                return new NextPickingResponseDto(Success: true, Message: "Items picked successfully.");
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }
    }
}