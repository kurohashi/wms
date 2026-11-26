using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.Contracts.Interfaces;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;
using Wms.Work.Application.Common.Helpers;

namespace Wms.PickList.Application.Command;

public sealed class FallbackPickPickListCommand : ICommand<FallbackPickListResponseDto>
{
    public string PickListNumber { get; }

    public FallbackPickPickListCommand(string pickListNumber)
    {
        PickListNumber = pickListNumber;
    }

    public FallbackPickPickListCommand(FallbackPickListRequestDto dto)
    {
        PickListNumber = dto?.PickListNumber ?? string.Empty;
    }

    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser, ILogger<Handler> logger)
        : ICommandHandler<FallbackPickPickListCommand, FallbackPickListResponseDto>
    {
        public async Task<FallbackPickListResponseDto> HandleAsync(FallbackPickPickListCommand command, CancellationToken ct = default)
        {
            long defaultWarehouseId = currentUser.WarehouseId ?? 0;

            if (string.IsNullOrWhiteSpace(command.PickListNumber))
                throw new BusinessRuleException("PickListNumber is required.");

            var rawInput = Uri.UnescapeDataString(command.PickListNumber ?? string.Empty);
            var distinctPickListNumbers = rawInput
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(num => !string.IsNullOrWhiteSpace(num))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctPickListNumbers.Count == 0)
                throw new BusinessRuleException("At least one valid PickListNumber is required.");

            var results = new List<FallbackPickListResultDto>();

            foreach (var rawNumber in distinctPickListNumbers)
            {
                var pickListNumber = rawNumber.Trim();
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
                try
                {
                    var utcNow = DateTime.UtcNow;

                    // 1. Try to find the picklist in the default warehouse
                    var pickList = await dbContext.PickLists
                        .FirstOrDefaultAsync(p => ((p.PickListNumber == rawNumber) && (p.WarehouseId == defaultWarehouseId) && (p.IsActive == true) && (p.IsDeleted == false)), ct);

                    // 2. If not found in default warehouse, check across all warehouses
                    if (pickList == null && defaultWarehouseId != 0)
                    {
                        pickList = await dbContext.PickLists
                        .FirstOrDefaultAsync(p => ((p.PickListNumber == rawNumber) && (p.WarehouseId == defaultWarehouseId) && (p.IsActive == true) && (p.IsDeleted == false)), ct);
                    }

                    if (pickList == null)
                    {
                        var anyPickList = await dbContext.PickLists
                        .FirstOrDefaultAsync(p => ((p.PickListNumber == rawNumber) && (p.WarehouseId == defaultWarehouseId) && (p.IsActive == true) && (p.IsDeleted == false)), ct);

                        if (anyPickList == null)
                        {
                            throw new BusinessRuleException($"Pick list '{pickListNumber}' does not exist in database.");
                        }

                        if (anyPickList.IsDeleted == true)
                        {
                            throw new BusinessRuleException($"Pick list '{pickListNumber}' is marked as deleted (IsDeleted=true).");
                        }

                        if (anyPickList.IsActive == false)
                        {
                            throw new BusinessRuleException($"Pick list '{pickListNumber}' is inactive (IsActive=false).");
                        }

                        throw new BusinessRuleException($"Pick list '{pickListNumber}' not found.");
                    }

                    var effectiveWarehouseId = pickList.WarehouseId;

                    if (pickList.Status == nameof(PickListStatus.CANCELLED))
                        throw new BusinessRuleException($"Pick list '{pickListNumber}' is cancelled.");

                    if (pickList.Status == nameof(PickListStatus.COMPLETED))
                        throw new BusinessRuleException($"Pick list '{pickListNumber}' is already completed.");

                    // Do not check or require workers for fallback picking
                    long? pickedByWorkerId = pickList.AssignedWorkerId;

                    // Fetch ALL PickListItems for the PickList
                    var pickListItems = await dbContext.PickListItems
                        .Include(pi => pi.PickList)
                        .Where(pi => pi.PickListId == pickList.Id
                                  && pi.IsActive != false
                                  && pi.IsDeleted != true)
                        .OrderBy(pi => pi.Id)
                        .ToListAsync(ct);

                    if (pickListItems.Count == 0)
                        throw new BusinessRuleException($"No active items found for pick list '{pickListNumber}'.");

                    var allPickListItemIds = pickListItems.Select(pi => pi.Id).ToList();

                    // Fetch ALL InventoryAllocations for these PickListItems
                    var allAllocations = await dbContext.InventoryAllocations
                        .Include(ia => ia.Batch)
                        .Where(ia => ia.PickListItemId != null
                                  && allPickListItemIds.Contains(ia.PickListItemId.Value)
                                  && ia.IsActive != false
                                  && ia.IsDeleted != true)
                        .OrderBy(ia => ia.PickListItemId)
                        .ThenBy(ia => ia.Sequence ?? ia.Id)
                        .ToListAsync(ct);

                    if (!allAllocations.Any())
                        throw new BusinessRuleException($"Inventory Allocations not found for pick list '{pickListNumber}'.");

                    var allSkuIdsArray = pickListItems.Select(pi => pi.SkuId).Distinct().ToArray();

                    // Fetch stock on hand for all SKUs in this picklist
                    var binStockDetails = await dbContext.Database
                        .SqlQuery<BinAvailableStockDetailDto>($"SELECT * FROM get_bin_available_stock_by_sku_list({allSkuIdsArray}, {effectiveWarehouseId}, NULL::bigint[], false)")
                        .ToListAsync(ct);

                    var allocatedBinIds = allAllocations
                        .Where(a => a.FromBin.HasValue)
                        .Select(a => a.FromBin!.Value)
                        .Distinct()
                        .ToList();

                    var allStockBinIds = binStockDetails.Select(b => b.BinId).Distinct().ToList();
                    var allRelevantBinIds = allocatedBinIds.Union(allStockBinIds).Distinct().ToList();

                    var bins = await dbContext.Bins
                        .Where(b => allRelevantBinIds.Contains(b.Id))
                        .ToListAsync(ct);

                    var binsById = bins.ToDictionary(b => b.Id);

                    // Check if any bin allocated for a SKU in this picklist is locked
                    var pickListItemSkuById = pickListItems.ToDictionary(pi => pi.Id, pi => pi.SkuId);

                    var products = await dbContext.Products
                        .AsNoTracking()
                        .Where(p => allSkuIdsArray.Contains(p.Sku) || allSkuIdsArray.Contains(p.Id))
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

                    var alreadyPickedItemIds = pickListItems
                        .Where(IsItemAlreadyPicked)
                        .Select(pi => pi.Id)
                        .ToHashSet();

                    var lockedBinErrors = allAllocations
                        .Where(a => a.FromBin.HasValue
                                 && a.PickListItemId.HasValue
                                 && !alreadyPickedItemIds.Contains(a.PickListItemId.Value)
                                 && !IsAllocationPicked(a)
                                 && pickListItemSkuById.ContainsKey(a.PickListItemId.Value)
                                 && binsById.TryGetValue(a.FromBin.Value, out var b)
                                 && b.IsLocked)
                        .Select(a =>
                        {
                            var bin = binsById[a.FromBin!.Value];
                            var skuId = pickListItemSkuById[a.PickListItemId!.Value];
                            var skuDisplay = GetSkuDisplay(skuId);
                            var binCode = !string.IsNullOrWhiteSpace(bin.Code) ? bin.Code : bin.Id.ToString();
                            return (BinCode: binCode, SkuDisplay: skuDisplay);
                        })
                        .Distinct()
                        .Select(x => $"Bin {x.BinCode} is locked for {x.SkuDisplay}")
                        .ToList();

                    if (lockedBinErrors.Count > 0)
                    {
                        var errorMessage = lockedBinErrors.Count == 1
                            ? $"cannot complete picklist as {lockedBinErrors[0]}"
                            : $"cannot complete picklist as {string.Join(", ", lockedBinErrors)}";

                        throw new BusinessRuleException(errorMessage);
                    }

                    // Fetch damage details
                    var damageDetails = await dbContext.PutawayRejections
                        .Where(pr => pr.PickListItemId.HasValue
                                  && allPickListItemIds.Contains(pr.PickListItemId.Value)
                                  && allSkuIdsArray.Contains(pr.SkuId ?? 0))
                        .GroupBy(pr => new { pr.SkuId, pr.Location.BinUuid })
                        .Select(g => new
                        {
                            SkuId = g.Key.SkuId,
                            BinUuid = g.Key.BinUuid,
                            TotalDamageQty = (long)g.Sum(pr => pr.Quantity)
                        })
                        .ToListAsync(ct);

                    var orderItemIds = allAllocations.Select(a => a.OrderItemId).Distinct().ToList();
                    var orderItems = await dbContext.WmsOrderItems
                        .Where(oi => orderItemIds.Contains(oi.Id))
                        .ToDictionaryAsync(oi => oi.Id, ct);

                    var allocIds = allAllocations.Select(a => a.Id).ToList();
                    var existingReservations = await dbContext.BinInventoryReservations
                        .Where(r => allocIds.Contains(r.InventoryAllocationId))
                        .ToListAsync(ct);

                    // Track remaining stock on hand per (BinId, SkuId)
                    var remainingStockByBinAndSku = new Dictionary<(long BinId, long SkuId), int>();
                    foreach (var stock in binStockDetails)
                    {
                        remainingStockByBinAndSku[(stock.BinId, stock.SkuId)] = Math.Max(0, stock.StockOnHand);
                    }

                    var pickExecutionlogs = new List<PickExecution>();
                    var affectedOrderIds = new HashSet<long>();
                    var allPickListOrderIds = new HashSet<long>();
                    var itemSummaries = new List<FallbackPickItemSummaryDto>();
                    var itemBinPicksByItemId = new Dictionary<long, Dictionary<long, int>>();
                    int totalPickedCount = 0;
                    int totalShortCount = 0;

                    // Process fulfillment per SKU group
                    var itemsBySku = pickListItems.GroupBy(pi => pi.SkuId);

                    foreach (var skuGroup in itemsBySku)
                    {
                        var skuId = skuGroup.Key;
                        var skuPickListItems = skuGroup.OrderBy(pi => pi.Id).ToList();
                        var skuPickListItemIds = skuPickListItems.Select(pi => pi.Id).ToHashSet();

                        // Allocations for this SKU, ordered by Order Priority, FEFO expiry date, Sequence, Id
                        var skuAllocations = allAllocations
                            .Where(ia => ia.PickListItemId.HasValue && skuPickListItemIds.Contains(ia.PickListItemId.Value))
                            .OrderBy(ia => ia.PickListItemId)
                            .ThenBy(ia => ia.Batch?.ExpiryDate ?? DateOnly.MaxValue)
                            .ThenBy(ia => ia.Sequence ?? ia.Id)
                            .ThenBy(ia => ia.Id)
                            .ToList();

                        var totalAllocatedQty = skuPickListItems.Sum(pi => (int)(pi.AllocatedQty ?? pi.RequestedQty));

                        // Track remaining quantity needed per PickListItem (accounting for already picked items/quantities)
                        var remainingNeededByItem = skuPickListItems.ToDictionary(
                            pi => pi.Id,
                            pi =>
                            {
                                if (IsItemAlreadyPicked(pi))
                                    return 0;

                                var alreadyPicked = (int)(pi.PickedQty ?? 0);
                                return Math.Max(0, (int)(pi.AllocatedQty ?? pi.RequestedQty) - alreadyPicked);
                            }
                        );

                        var pickedQtyByAllocId = skuAllocations.ToDictionary(
                            a => a.Id,
                            a =>
                            {
                                if (IsAllocationPicked(a))
                                    return a.PickedQty.HasValue && a.PickedQty.Value > 0 ? a.PickedQty.Value : a.AllocatedQty;

                                if (a.PickListItemId.HasValue && alreadyPickedItemIds.Contains(a.PickListItemId.Value))
                                    return a.AllocatedQty;

                                return Math.Max(0, a.PickedQty ?? 0);
                            }
                        );

                        // Phase 1: Primary Assigned Bin Consumption
                        // For each allocation, picks min(StockOnHand, allocNeeded)
                        foreach (var alloc in skuAllocations)
                        {
                            var itemId = alloc.PickListItemId!.Value;
                            var itemStillNeeds = remainingNeededByItem[itemId];
                            if (itemStillNeeds <= 0) continue;

                            var allocAlreadyPicked = pickedQtyByAllocId[alloc.Id];
                            var allocNeeded = Math.Min(alloc.AllocatedQty - allocAlreadyPicked, itemStillNeeds);
                            if (allocNeeded <= 0) continue;

                            if (alloc.FromBin.HasValue)
                            {
                                var binId = alloc.FromBin.Value;
                                binsById.TryGetValue(binId, out var bin);
                                var isLocked = bin?.IsLocked ?? false;

                                int primaryAvail = 0;
                                if (!isLocked)
                                {
                                    if (bin != null && (bin.IsClusterBased || bin.IsRoutable) && !bin.IsPickable)
                                    {
                                        var damageDetail = damageDetails.FirstOrDefault(b => b.SkuId == skuId && b.BinUuid == bin.BinUuid);
                                        var damageQty = (int)(damageDetail?.TotalDamageQty ?? 0);
                                        primaryAvail = Math.Max(0, allocNeeded - damageQty);
                                    }
                                    else
                                    {
                                        remainingStockByBinAndSku.TryGetValue((binId, skuId), out primaryAvail);
                                    }
                                }

                                if (primaryAvail > 0)
                                {
                                    var takeFromPrimary = Math.Min(primaryAvail, allocNeeded);
                                    pickedQtyByAllocId[alloc.Id] += takeFromPrimary;
                                    if (remainingStockByBinAndSku.ContainsKey((binId, skuId)))
                                        remainingStockByBinAndSku[(binId, skuId)] -= takeFromPrimary;
                                    remainingNeededByItem[itemId] -= takeFromPrimary;

                                    if (!itemBinPicksByItemId.ContainsKey(itemId))
                                        itemBinPicksByItemId[itemId] = new Dictionary<long, int>();
                                    itemBinPicksByItemId[itemId][binId] = itemBinPicksByItemId[itemId].GetValueOrDefault(binId, 0) + takeFromPrimary;

                                    pickExecutionlogs.Add(new PickExecution
                                    {
                                        PickListItemId = itemId,
                                        PickedBy = pickedByWorkerId,
                                        SourceBinId = binId,
                                        PickedQty = takeFromPrimary,
                                        ShortQty = alloc.AllocatedQty - pickedQtyByAllocId[alloc.Id],
                                        IsActive = true,
                                        IsDeleted = false,
                                        CreatedDate = utcNow
                                    });

                                    var reservation = existingReservations
                                        .FirstOrDefault(r => r.InventoryAllocationId == alloc.Id && r.BinId == binId);

                                    if (reservation != null)
                                    {
                                        reservation.PickedQty = pickedQtyByAllocId[alloc.Id];
                                        if (reservation.ReservedQty == 0 && reservation.PickedQty > 0)
                                            reservation.ReservedQty = reservation.PickedQty.Value;
                                        reservation.Status = nameof(InventoryStatus.PICKED);
                                        reservation.UpdatedAt = utcNow;
                                    }
                                }
                                else
                                {
                                    var reservation = existingReservations
                                        .FirstOrDefault(r => r.InventoryAllocationId == alloc.Id && r.BinId == binId);

                                    if (reservation != null && (reservation.PickedQty ?? 0) == 0 && pickedQtyByAllocId[alloc.Id] == 0)
                                    {
                                        reservation.PickedQty = 0;
                                        reservation.Status = nameof(InventoryStatus.PICKED);
                                        reservation.UpdatedAt = utcNow;
                                    }
                                }
                            }
                        }

                        // Phase 2: Fallback / Multi-bin Consumption (When primary bin stock is insufficient)
                        // If an item needs more qty than was available in its assigned bin(s),
                        // consume from other available bins with stock on hand for this SKU.
                        foreach (var alloc in skuAllocations)
                        {
                            var itemId = alloc.PickListItemId!.Value;
                            var itemStillNeeds = remainingNeededByItem[itemId];
                            if (itemStillNeeds <= 0) continue;

                            var allocAlreadyPicked = pickedQtyByAllocId[alloc.Id];
                            var allocNeeded = Math.Min(alloc.AllocatedQty - allocAlreadyPicked, itemStillNeeds);
                            if (allocNeeded <= 0) continue;

                            orderItems.TryGetValue(alloc.OrderItemId, out var orderItemForAlloc);

                            // Prioritize: other allocated bins for this SKU first, pickable bins next, sequence order
                            var availableBins = remainingStockByBinAndSku
                                .Where(kv => kv.Key.SkuId == skuId && kv.Value > 0)
                                .Select(kv => kv.Key.BinId)
                                .Distinct()
                                .Select(binId => binsById.TryGetValue(binId, out var b) ? b : null)
                                .Where(b => b != null && !b.IsLocked && b.IsActive == true && b.IsDeleted != true)
                                .OrderByDescending(b => allocatedBinIds.Contains(b!.Id))
                                .ThenByDescending(b => b!.IsPickable)
                                .ThenBy(b => b!.Sequence ?? int.MaxValue)
                                .Select(b => b!.Id)
                                .ToList();

                            foreach (var binId in availableBins)
                            {
                                if (allocNeeded == 0) break;

                                var otherAvail = remainingStockByBinAndSku.GetValueOrDefault((binId, skuId), 0);
                                if (otherAvail <= 0) continue;

                                var takeFromOther = Math.Min(otherAvail, allocNeeded);

                                pickedQtyByAllocId[alloc.Id] += takeFromOther;
                                allocNeeded -= takeFromOther;
                                remainingStockByBinAndSku[(binId, skuId)] -= takeFromOther;
                                remainingNeededByItem[itemId] -= takeFromOther;

                                if (!itemBinPicksByItemId.ContainsKey(itemId))
                                    itemBinPicksByItemId[itemId] = new Dictionary<long, int>();
                                itemBinPicksByItemId[itemId][binId] = itemBinPicksByItemId[itemId].GetValueOrDefault(binId, 0) + takeFromOther;

                                pickExecutionlogs.Add(new PickExecution
                                {
                                    PickListItemId = itemId,
                                    PickedBy = pickedByWorkerId,
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
                                    else
                                    {
                                        var newReservation = new BinInventoryReservation
                                        {
                                            InventoryAllocationId = alloc.Id,
                                            BinId = binId,
                                            ReservedQty = takeFromOther,
                                            PickedQty = takeFromOther,
                                            Skuid = skuId,
                                            OrderItemId = alloc.OrderItemId,
                                            Status = nameof(InventoryStatus.PICKED),
                                            CreatedAt = utcNow,
                                            UpdatedAt = utcNow,
                                            IsSold = false
                                        };
                                        await dbContext.BinInventoryReservations.AddAsync(newReservation, ct);
                                        existingReservations.Add(newReservation);
                                    }
                                }
                            }
                        }

                        // Finalize allocation statuses for this SKU
                        foreach (var alloc in skuAllocations)
                        {
                            var allocPicked = pickedQtyByAllocId[alloc.Id];
                            alloc.PickedQty = allocPicked;
                            alloc.Status = (allocPicked >= alloc.AllocatedQty || IsAllocationPicked(alloc))
                                ? nameof(WorkerItemPickStatus.COMPLETED)
                                : nameof(WorkerItemPickStatus.SHORT_PICK);
                            alloc.ModifiedDate = utcNow;
                        }

                        // Waterfall distribution across PickListItems
                        foreach (var item in skuPickListItems)
                        {
                            var itemAllocated = (int)(item.AllocatedQty ?? item.RequestedQty);
                            var allocSum = skuAllocations
                                .Where(ia => ia.PickListItemId == item.Id)
                                .Sum(ia => ia.PickedQty ?? 0);

                            var existingItemPicked = (int)(item.PickedQty ?? 0);
                            var itemPicked = Math.Max(allocSum, existingItemPicked);
                            var itemShort = Math.Max(0, itemAllocated - itemPicked);

                            item.PickedQty = itemPicked;
                            item.Status = (itemShort == 0 || IsItemAlreadyPicked(item))
                                ? nameof(WorkerItemPickStatus.COMPLETED)
                                : nameof(WorkerItemPickStatus.SHORT_PICK);
                            item.ModifiedDate = utcNow;

                            totalPickedCount += itemPicked;
                            totalShortCount += itemShort;

                            var binPickList = itemBinPicksByItemId.GetValueOrDefault(item.Id)?
                                .Select(kv =>
                                {
                                    binsById.TryGetValue(kv.Key, out var bin);
                                    var originalStock = binStockDetails
                                        .FirstOrDefault(b => b.BinId == kv.Key && b.SkuId == item.SkuId)?.StockOnHand ?? 0;
                                    return new FallbackBinPickDetailDto(
                                        BinId: kv.Key,
                                        BinUuid: bin?.BinUuid ?? Guid.Empty,
                                        BinCode: bin?.Code ?? string.Empty,
                                        PickedQty: kv.Value,
                                        StockOnHand: originalStock
                                    );
                                }).ToList();

                            if ((binPickList == null || binPickList.Count == 0) && itemPicked > 0)
                            {
                                var itemAllocIds = skuAllocations
                                    .Where(ia => ia.PickListItemId == item.Id)
                                    .Select(ia => ia.Id)
                                    .ToHashSet();

                                binPickList = existingReservations
                                    .Where(r => itemAllocIds.Contains(r.InventoryAllocationId) && (r.PickedQty ?? 0) > 0)
                                    .Select(r =>
                                    {
                                        binsById.TryGetValue(r.BinId, out var bin);
                                        var originalStock = binStockDetails
                                            .FirstOrDefault(b => b.BinId == r.BinId && b.SkuId == item.SkuId)?.StockOnHand ?? 0;
                                        return new FallbackBinPickDetailDto(
                                            BinId: r.BinId,
                                            BinUuid: bin?.BinUuid ?? Guid.Empty,
                                            BinCode: bin?.Code ?? string.Empty,
                                            PickedQty: r.PickedQty ?? 0,
                                            StockOnHand: originalStock
                                        );
                                    }).ToList();
                            }

                            itemSummaries.Add(new FallbackPickItemSummaryDto(
                                item.PickListItemUuid,
                                item.SkuId,
                                itemAllocated,
                                itemPicked,
                                itemShort,
                                item.Status,
                                binPickList ?? new List<FallbackBinPickDetailDto>()
                            ));
                        }
                    }

                    // Finalize any untouched reservations (e.g. unpicked allocations)
                    foreach (var reservation in existingReservations.Where(r => r.Status != nameof(InventoryStatus.PICKED)))
                    {
                        var relatedAlloc = allAllocations.FirstOrDefault(a => a.Id == reservation.InventoryAllocationId);
                        var pickedQty = relatedAlloc != null ? (relatedAlloc.PickedQty ?? 0) : 0;
                        reservation.PickedQty = pickedQty > 0 ? pickedQty : 0;
                        reservation.Status = nameof(InventoryStatus.PICKED);
                        reservation.UpdatedAt = utcNow;
                    }

                    if (pickExecutionlogs.Count > 0)
                    {
                        await dbContext.PickExecutions.AddRangeAsync(pickExecutionlogs, ct);
                    }

                    // Save allocations, pick list items, reservations, and pick executions so sibling queries see updated state
                    await dbContext.SaveChangesAsync(ct);

                    // Update WmsOrderItems for all affected order items in this picklist
                    var distinctOrderItemIds = pickListItems
                        .Where(pi => pi.OrderItemId.HasValue)
                        .Select(pi => pi.OrderItemId!.Value)
                        .Distinct()
                        .ToList();

                    foreach (var orderItemId in distinctOrderItemIds)
                    {
                        var orderItem = await dbContext.WmsOrderItems
                            .FirstOrDefaultAsync(oi => oi.Id == orderItemId, ct);

                        if (orderItem != null)
                        {
                            allPickListOrderIds.Add(orderItem.OrderId);
                            var currentDropLocationId = pickList.DropLocationId;

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

                    // Complete the overall PickList
                    var userId = currentUser.UserId ?? currentUser.Name ?? "system";
                    pickList.Status = nameof(PickListStatus.COMPLETED);
                    pickList.ModifiedDate = utcNow;
                    pickList.ModifiedBy = userId;

                    // Ensure navigation property PickListItems is populated
                    if (pickList.PickListItems == null || pickList.PickListItems.Count == 0)
                    {
                        pickList.PickListItems = pickListItems;
                    }

                    //if (pickList.DropLocationId.HasValue)
                    //{
                    //    await dbContext.CreateDropZoneAllocationsAsync(pickList, allAllocations, nameof(PickListStatus.COMPLETED), utcNow, ct);
                    //}

                    // Complete associated Tasks and Assignments for this pick list
                    var tasks = await dbContext.Tasks
                        .Include(t => t.Assignments)
                            .ThenInclude(a => a.Worker)
                        .Include(t => t.Assignments)
                            .ThenInclude(a => a.WorkBatch)
                        .Where(t => !t.IsDeleted
                                 && (t.ReferenceId == pickList.PickListUuid
                                     || (t.WarehouseId == effectiveWarehouseId
                                         && (t.OrderRef == pickList.PickListNumber
                                             || (t.TaskName != null && pickList.PickListNumber != null && (t.TaskName == $"Pick-{pickList.PickListNumber}"
                                                                     || t.TaskName == $"Pick - {pickList.PickListNumber}"
                                                                     || t.TaskName.Contains(pickList.PickListNumber)))))))
                        .ToListAsync(ct);

                    var taskIds = tasks.Select(t => t.Id).Distinct().ToList();

                    var directAssignments = taskIds.Count > 0
                        ? await dbContext.Assignments
                            .Include(a => a.Worker)
                            .Include(a => a.WorkBatch)
                            .Where(a => taskIds.Contains(a.TaskId) && !a.IsDeleted)
                            .ToListAsync(ct)
                        : new List<Assignment>();

                    var allAssignments = tasks.SelectMany(t => t.Assignments)
                        .Union(directAssignments)
                        .DistinctBy(a => a.Id)
                        .ToList();

                    var affectedWorkerUuids = new List<Guid>();

                    foreach (var task in tasks)
                    {
                        task.Status = nameof(TaskStatusEnum.COMPLETED);
                        task.ModifiedDate = utcNow;
                        task.ModifiedBy = userId;
                    }

                    foreach (var assignment in allAssignments)
                    {
                        assignment.Status = nameof(AssignmentStatusEnum.COMPLETED);
                        assignment.CompletedAt = utcNow;
                        assignment.ModifiedDate = utcNow;
                        assignment.ModifiedBy = userId;

                        var start = assignment.StartedAt ?? assignment.AssignedAt;
                        if (start != default)
                        {
                            assignment.ActualSeconds = (decimal)(utcNow - start).TotalSeconds;
                        }
                        else
                        {
                            assignment.StartedAt = utcNow;
                            assignment.ActualSeconds = 0;
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

                    // Check work batches that can now be closed
                    var workBatchIds = allAssignments
                        .Where(a => a.WorkBatchId.HasValue)
                        .Select(a => a.WorkBatchId!.Value)
                        .Distinct()
                        .ToList();

                    var completedAssignmentIds = allAssignments.Select(a => a.Id).ToHashSet();
                    foreach (var workBatchId in workBatchIds)
                    {
                        var hasPendingInBatch = await dbContext.Assignments
                            .AnyAsync(a => a.WorkBatchId == workBatchId
                                        && !a.IsDeleted && a.IsActive
                                        && !completedAssignmentIds.Contains(a.Id)
                                        && a.Status != nameof(AssignmentStatusEnum.COMPLETED), ct);

                        if (!hasPendingInBatch)
                        {
                            var workBatches = await dbContext.WorkBatches
                                .Where(wb => wb.Id == workBatchId && !wb.IsDeleted && wb.IsActive)
                                .ToListAsync(ct);

                            foreach (var wb in workBatches)
                            {
                                wb.Status = nameof(WorkBatchEnum.CLOSED);
                                wb.ModifiedDate = utcNow;
                                wb.ModifiedBy = userId;
                            }
                        }
                    }

                    if (affectedWorkerUuids.Count > 0)
                    {
                        try
                        {
                            await dbContext.SyncWorkersAvailabilityAsync(
                                affectedWorkerUuids.Distinct(),
                                userId,
                                utcNow,
                                ct);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to sync worker availability for pick list '{PickListNumber}'.", pickListNumber);
                        }
                    }

                    var ordersToEvaluate = allPickListOrderIds.Union(affectedOrderIds).Distinct().ToList();

                    var dropBin = pickList.DropLocationId.HasValue
                        ? await dbContext.Bins.AsNoTracking().FirstOrDefaultAsync(b => b.Id == pickList.DropLocationId.Value, ct)
                        : null;
                    bool isRoutePicklist = dropBin != null && dropBin.IsRoutable;

                    // Complete non-picklist pending items for affected orders, complete OrderStageProgress, and promote RouteOrders ONLY for route picklists
                    var uniqueAffectedOrderIds = affectedOrderIds.Distinct().ToList();
                    if (isRoutePicklist && uniqueAffectedOrderIds.Count > 0)
                    {
                        var nonPicklistOrderItems = await dbContext.WmsOrderItems
                            .Where(oi => uniqueAffectedOrderIds.Contains(oi.OrderId)
                                         && oi.Status == nameof(OrderItemStatusEnum.PENDING)
                                         && !dbContext.PickListItems.Any(pi => pi.OrderItemId == oi.Id && pi.IsActive == true && pi.IsDeleted == false))
                            .ToListAsync(ct);

                        foreach (var pendingItem in nonPicklistOrderItems)
                        {
                            pendingItem.Status = nameof(OrderItemStatusEnum.PICKING_COMPLETED);
                            pendingItem.PickedQuantity = 0;
                            pendingItem.UpdatedAt = utcNow;
                        }

                        // Check which orders have all items PICKING_COMPLETED
                        var incompleteOrders = await dbContext.WmsOrderItems
                            .Where(i => uniqueAffectedOrderIds.Contains(i.OrderId)
                                        && (i.Status == null || i.Status != nameof(OrderItemStatusEnum.PICKING_COMPLETED)))
                            .Select(i => i.OrderId)
                            .Distinct()
                            .ToListAsync(ct);

                        var fullyCompletedOrderIds = uniqueAffectedOrderIds
                            .Except(incompleteOrders)
                            .ToList();

                        if (fullyCompletedOrderIds.Count > 0)
                        {
                            var stageProgresses = await dbContext.OrderStageProgresses
                                .Where(p => fullyCompletedOrderIds.Contains(p.OrderId) && p.Stage == nameof(OrderStageEnum.ROUTE))
                                .ToListAsync(ct);

                            foreach (var progress in stageProgresses)
                            {
                                var totalItems = await dbContext.WmsOrderItems.CountAsync(i => i.OrderId == progress.OrderId, ct);
                                if (progress.ExpectedItems <= 0 && totalItems > 0)
                                {
                                    progress.ExpectedItems = totalItems;
                                }
                                progress.CompletedItems = progress.ExpectedItems;
                                progress.Status = nameof(OrderStageStatusEnum.COMPLETED);
                                progress.UpdatedDate = utcNow;
                            }

                            var existingWithProgress = stageProgresses.Select(p => p.OrderId).ToHashSet();
                            var missingProgressOrderIds = fullyCompletedOrderIds.Where(id => !existingWithProgress.Contains(id)).ToList();
                            foreach (var missingOrderId in missingProgressOrderIds)
                            {
                                var itemCount = await dbContext.WmsOrderItems.CountAsync(i => i.OrderId == missingOrderId, ct);
                                await dbContext.OrderStageProgresses.AddAsync(new OrderStageProgress
                                {
                                    OrderId = missingOrderId,
                                    Stage = nameof(OrderStageEnum.ROUTE),
                                    ExpectedItems = itemCount,
                                    CompletedItems = itemCount,
                                    Status = nameof(OrderStageStatusEnum.COMPLETED),
                                    CreatedDate = utcNow,
                                    UpdatedDate = utcNow
                                }, ct);
                            }

                            var routeOrdersToPromote = await dbContext.RouteOrders
                                .Include(ro => ro.Order)
                                .Where(ro => ro.OrderId.HasValue && fullyCompletedOrderIds.Contains(ro.OrderId.Value) && ro.IsActive == true && ro.IsDeleted != true)
                                .ToListAsync(ct);

                            var candidateRouteIds = routeOrdersToPromote.Select(ro => ro.RouteId).Distinct().ToList();

                            var ordersToPromote = await dbContext.WmsOrders
                                .Where(o => fullyCompletedOrderIds.Contains(o.Id))
                                .ToListAsync(ct);

                            var ordersMap = ordersToPromote.ToDictionary(o => o.Id);



                            foreach (var ro in routeOrdersToPromote)
                            {
                                bool isTransferOrder = (ro.OrderId.HasValue && ordersMap.TryGetValue(ro.OrderId.Value, out var o) && (string.Equals(o.CustomerType, "TransferOrder", StringComparison.OrdinalIgnoreCase) || o.TransferOrderId.HasValue))
                                    || string.Equals(ro.Order?.CustomerType, "TransferOrder", StringComparison.OrdinalIgnoreCase)
                                    || (ro.Order?.TransferOrderId.HasValue ?? false);

                                if (isTransferOrder)
                                {
                                    ro.Status = nameof(RouteStatusEnum.SORTING_COMPLETED);
                                    ro.UpdatedDate = utcNow;
                                }
                                else if (ro.Status != nameof(RouteStatusEnum.SORTING_COMPLETED))
                                {
                                    ro.Status = nameof(RouteOrderStatusEnum.READY_FOR_SORT);
                                    ro.UpdatedDate = utcNow;
                                }
                            }

                            if (candidateRouteIds.Count > 0)
                            {
                                var candidateRoutes = await dbContext.RouteMasters
                                    .Where(rm => candidateRouteIds.Contains(rm.Id) && rm.IsActive == true && rm.IsDeleted != true)
                                    .ToListAsync(ct);

                                var allOrdersForCandidateRoutes = await dbContext.RouteOrders
                                    .Include(ro => ro.Order)
                                    .Where(ro => candidateRouteIds.Contains(ro.RouteId) && ro.IsActive == true && ro.IsDeleted != true)
                                    .ToListAsync(ct);

                                foreach (var r in candidateRoutes)
                                {
                                    var linked = allOrdersForCandidateRoutes.Where(ro => ro.RouteId == r.Id).ToList();
                                    bool isTransferRoute = linked.Any(ro =>
                                        (ro.Order != null && (string.Equals(ro.Order.CustomerType, "TransferOrder", StringComparison.OrdinalIgnoreCase) || ro.Order.TransferOrderId.HasValue)) ||
                                        (ro.OrderId.HasValue && ordersMap.TryGetValue(ro.OrderId.Value, out var o) && (string.Equals(o.CustomerType, "TransferOrder", StringComparison.OrdinalIgnoreCase) || o.TransferOrderId.HasValue)));

                                    if (isTransferRoute)
                                    {
                                        r.CompletedOrders = linked.Count(ro => ro.Status == nameof(RouteStatusEnum.SORTING_COMPLETED));
                                        if (linked.Count > 0 && linked.All(ro => ro.Status == nameof(RouteStatusEnum.SORTING_COMPLETED)))
                                        {
                                            r.Status = nameof(RouteStatusEnum.SORTING_COMPLETED);
                                            r.UpdatedDate = utcNow;
                                        }
                                    }
                                    else
                                    {
                                        r.CompletedOrders = linked.Count(ro => ro.Status == nameof(RouteOrderStatusEnum.READY_FOR_SORT) || ro.Status == nameof(RouteStatusEnum.SORTING_COMPLETED));
                                        if (linked.Count > 0 && linked.All(ro => ro.Status == nameof(RouteOrderStatusEnum.READY_FOR_SORT) || ro.Status == nameof(RouteStatusEnum.SORTING_COMPLETED)))
                                        {
                                            if (r.Status != nameof(RouteStatusEnum.SORTING_COMPLETED) && r.Status != nameof(RouteStatusEnum.COMPLETED))
                                            {
                                                r.Status = nameof(RouteStatusEnum.READY_FOR_SORT);
                                                r.UpdatedDate = utcNow;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else if (!isRoutePicklist && uniqueAffectedOrderIds.Count > 0)
                    {
                        // For non-route (e.g. cluster) picklists, complete CLUSTER stage progress only
                        var clusterStageProgresses = await dbContext.OrderStageProgresses
                            .Where(p => uniqueAffectedOrderIds.Contains(p.OrderId) && p.Stage == nameof(OrderStageEnum.CLUSTER))
                            .ToListAsync(ct);

                        foreach (var progress in clusterStageProgresses)
                        {
                            if (progress.ExpectedItems <= 0)
                            {
                                var itemCount = await dbContext.WmsOrderItems.CountAsync(i => i.OrderId == progress.OrderId, ct);
                                progress.ExpectedItems = itemCount;
                            }
                            progress.CompletedItems = progress.ExpectedItems;
                            progress.Status = nameof(OrderStageStatusEnum.COMPLETED);
                            progress.UpdatedDate = utcNow;
                        }
                    }

                    // Evaluate and update order statuses based on picklist types and completion
                    if (ordersToEvaluate.Count > 0)
                    {
                        var ordersToUpdate = await dbContext.WmsOrders
                            .Where(o => ordersToEvaluate.Contains(o.Id))
                            .ToListAsync(ct);

                        var orderPickLists = await (
                            from oi in dbContext.WmsOrderItems
                            join pi in dbContext.PickListItems on oi.Id equals pi.OrderItemId
                            join pl in dbContext.PickLists on pi.PickListId equals pl.Id
                            join bin in dbContext.Bins on pl.DropLocationId equals bin.Id into binJoin
                            from bin in binJoin.DefaultIfEmpty()
                            where ordersToEvaluate.Contains(oi.OrderId)
                                  && (pi.IsActive == true && pi.IsDeleted != true)
                                  && (pl.IsActive == true && pl.IsDeleted != true)
                                  && pl.Status != nameof(PickListStatus.CANCELLED)
                            select new
                            {
                                OrderId = oi.OrderId,
                                PickListId = pl.Id,
                                PickListStatus = pl.Status,
                                IsClusterBased = bin != null && bin.IsClusterBased,
                                IsRoutable = bin != null && bin.IsRoutable
                            }
                        ).Distinct().ToListAsync(ct);

                        foreach (var ord in ordersToUpdate)
                        {
                            // 5. If Order Status is READY_FOR_SORT then don't change its status. Also ignore CANCELLED and INVOICED.
                            if (string.Equals(ord.OrderStatus, nameof(WmsOrderStatusEnum.READY_FOR_SORT), StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ord.OrderStatus, nameof(WmsOrderStatusEnum.CANCELLED), StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ord.OrderStatus, nameof(WmsOrderStatusEnum.INVOICED), StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var currentOrderPickLists = orderPickLists
                                .Where(p => p.OrderId == ord.Id)
                                .GroupBy(p => p.PickListId)
                                .Select(g => g.First())
                                .ToList();

                            if (currentOrderPickLists.Count == 0)
                            {
                                continue;
                            }

                            bool hasRoutePicklist = currentOrderPickLists.Any(p => p.IsRoutable);
                            bool allPickListsCompleted = currentOrderPickLists.All(p =>
                                p.PickListId == pickList.Id
                                || string.Equals(p.PickListStatus, nameof(PickListStatus.COMPLETED), StringComparison.OrdinalIgnoreCase)
                                || string.Equals(p.PickListStatus, nameof(PickListStatus.SHORT_PICK), StringComparison.OrdinalIgnoreCase));

                            bool isTransferOrder = string.Equals(ord.CustomerType, "TransferOrder", StringComparison.OrdinalIgnoreCase) || ord.TransferOrderId.HasValue;

                            if (hasRoutePicklist)
                            {
                                if (allPickListsCompleted)
                                {
                                    // 4. If order contains atleast one route picklist and all are completed then status to ROUTE_PICKING_COMPLETED (or INVOICED for transfer orders)
                                    if (isTransferOrder)
                                    {
                                        ord.OrderStatus = nameof(WmsOrderStatusEnum.INVOICED);
                                    }
                                    else
                                    {
                                        ord.OrderStatus = nameof(WmsOrderStatusEnum.ROUTE_PICKING_COMPLETED);
                                    }
                                }
                                else
                                {
                                    // 3. If order contains atleast one route picklist and atleast one is not completed then status to ROUTE_PICKING_IN_PROGRESS
                                    ord.OrderStatus = nameof(WmsOrderStatusEnum.ROUTE_PICKING_IN_PROGRESS);
                                }
                                ord.UpdatedAt = utcNow;
                            }
                            else
                            {
                                // Contains no route picklist (all cluster picklists)
                                if (allPickListsCompleted)
                                {
                                    // 2. If all cluster picklists are completed and contains no route picklist then change status to CLUSTER_PICKING_COMPLETED (or INVOICED for transfer orders)
                                    if (isTransferOrder)
                                    {
                                        ord.OrderStatus = nameof(WmsOrderStatusEnum.INVOICED);
                                    }
                                    else
                                    {
                                        ord.OrderStatus = nameof(WmsOrderStatusEnum.CLUSTER_PICKING_COMPLETED);
                                    }
                                }
                                else
                                {
                                    // 1. If order contains all cluster picklist and atleast one is not completed, change order status to CLUSTER_PICKING_IN_PROGRESS
                                    ord.OrderStatus = nameof(WmsOrderStatusEnum.CLUSTER_PICKING_IN_PROGRESS);
                                }
                                ord.UpdatedAt = utcNow;
                            }
                        }
                    }

                    await dbContext.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    dbContext.ChangeTracker.Clear();

                    if (affectedOrderIds.Count > 0 && OrderPickingCompletedQueueAccessor.Instance is { } pickingCompletedQueue)
                    {
                        foreach (var orderId in affectedOrderIds)
                        {
                            await pickingCompletedQueue.EnqueueAsync(orderId, ct);
                        }
                    }

                    results.Add(new FallbackPickListResultDto(
                        Success: true,
                        Message: $"Pick list '{pickList.PickListNumber}' processed successfully via fallback picking.",
                        PickListNumber: pickList.PickListNumber ?? pickListNumber,
                        PickListUuid: pickList.PickListUuid,
                        PickListStatus: pickList.Status,
                        TotalItemsProcessed: itemSummaries.Count,
                        TotalPickedQty: totalPickedCount,
                        TotalShortQty: totalShortCount,
                        PickedItems: itemSummaries
                    ));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(ct);
                    dbContext.ChangeTracker.Clear();

                    var errorMessage = ex.InnerException != null
                        ? $"{ex.Message} -> {ex.InnerException.Message}"
                        : ex.Message;

                    logger.LogError(ex, "Fallback picking failed for PickList '{PickListNumber}'. Error: {ErrorMessage}", pickListNumber, errorMessage);

                    results.Add(new FallbackPickListResultDto(
                        Success: false,
                        Message: errorMessage,
                        PickListNumber: pickListNumber,
                        PickListUuid: null,
                        PickListStatus: null,
                        TotalItemsProcessed: 0,
                        TotalPickedQty: 0,
                        TotalShortQty: 0,
                        PickedItems: new List<FallbackPickItemSummaryDto>()
                    ));

                    if (distinctPickListNumbers.Count == 1 && ex is BusinessRuleException)
                    {
                        throw;
                    }
                }
            }

            var totalSucceeded = results.Count(r => r.Success);
            var totalFailed = results.Count(r => !r.Success);
            var overallSuccess = totalSucceeded > 0;
            var summaryMessage = distinctPickListNumbers.Count == 1
                ? results[0].Message
                : $"{totalSucceeded} of {distinctPickListNumbers.Count} pick lists processed successfully. ({totalFailed} failed)";

            return new FallbackPickListResponseDto(
                Success: overallSuccess,
                Message: summaryMessage,
                TotalPickListsRequested: distinctPickListNumbers.Count,
                TotalPickListsSucceeded: totalSucceeded,
                TotalPickListsFailed: totalFailed,
                Results: results
            );
        }

        private static bool IsItemAlreadyPicked(PickListItem item)
        {
            if (string.Equals(item.Status, nameof(WorkerItemPickStatus.COMPLETED), StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Status, nameof(PickListItemStatus.PICKED), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var allocatedQty = (int)(item.AllocatedQty ?? item.RequestedQty);
            return allocatedQty > 0 && (item.PickedQty ?? 0) >= allocatedQty;
        }

        private static bool IsAllocationPicked(InventoryAllocation alloc)
        {
            if (string.Equals(alloc.Status, nameof(WorkerItemPickStatus.COMPLETED), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return alloc.AllocatedQty > 0 && (alloc.PickedQty ?? 0) >= alloc.AllocatedQty;
        }
    }
}
