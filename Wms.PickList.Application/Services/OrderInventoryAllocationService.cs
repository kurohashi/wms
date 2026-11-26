using Microsoft.EntityFrameworkCore;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Services;

/// <summary>
/// Allocates pickable bin stock (FEFO) to order items, persists InventoryAllocation rows,
/// and increments LocationInventoryBatch.HoldQuantity for each allocated quantity.
/// Warehouse-level inventory is already deducted at order placement.
/// </summary>
public sealed class OrderInventoryAllocationService(WmsDbContextWrite dbContext)
{
    private sealed record BinStockRow(
        LocationInventoryBatch LocationBatch,
        long BinId,
        DateOnly? BatchExpiry,
        bool IsPickable,
        decimal AvailableQty,
        decimal AvailableQtyForNewOrders);

    public async Task<AllocateOrderInventoryResult> AllocateAsync(long orderId, CancellationToken ct = default)
    {
        var order = await dbContext.WmsOrders
            .Include(o => o.WmsOrderItems)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
            return new AllocateOrderInventoryResult(orderId, 0, 0, SkippedAlreadyAllocated: false);

        var distinctOrderItems = order.WmsOrderItems.DistinctBy(i => i.Id).ToList();
        var orderItemIds = distinctOrderItems.Select(i => i.Id).ToList();
        var hasExistingAllocations = await dbContext.InventoryAllocations
            .AnyAsync(
                a => a.OrderItemId != null
                     && orderItemIds.Contains(a.OrderItemId)
                     && a.IsDeleted != true,
                ct);

        if (hasExistingAllocations)
            return new AllocateOrderInventoryResult(orderId, 0, 0, SkippedAlreadyAllocated: true);

        var skuIds = distinctOrderItems.Select(i => i.Sku).Distinct().ToList();

        // Get all pickable bin stock for the order's SKUs, ordered by FEFO (earliest expiry first)
        // locationinventorybatches.availablequantity - SUM(BinInventoryReservation.PickedQty)
        var binStockBySku = await LoadTrackedBinStockBySkuWithReservationsAsync(order.WarehouseId, skuIds, ct);

        if (binStockBySku.Count == 0)
            return new AllocateOrderInventoryResult(orderId, distinctOrderItems.Count, 0, SkippedAlreadyAllocated: false);

        var remainingByBatchId = binStockBySku.Values
            .SelectMany(rows => rows)
            .DistinctBy(row => row.LocationBatch.Id)
            .ToDictionary(
                row => row.LocationBatch.Id,
                row => row.AvailableQty);

        var nowUtc = DateTime.UtcNow;
        var allocationsCreated = 0;
        var sequenceByOrderItem = new Dictionary<long, int>();
        var processedOrderItemIds = new HashSet<long>();

        foreach (var orderItem in distinctOrderItems)
        {
            if (orderItem.TotalItemQuantity <= 0 || !processedOrderItemIds.Add(orderItem.Id))
                continue;

            if (!binStockBySku.TryGetValue(orderItem.Sku, out var binStockRows) || binStockRows.Count == 0)
                continue;

            var remaining = (decimal)orderItem.TotalItemQuantity;

            foreach (var stockRow in binStockRows)
            {
                if (remaining <= 0)
                    break;

                var locationBatchId = stockRow.LocationBatch.Id;
                if (!remainingByBatchId.TryGetValue(locationBatchId, out var available) || available <= 0)
                    continue;

                var take = Math.Min(available, remaining);
                if (take <= 0)
                    continue;

                if (!sequenceByOrderItem.TryGetValue(orderItem.Id, out var sequence))
                    sequence = 1;

                var inventories = new InventoryAllocation
                {
                    OrderItemId = orderItem.Id,
                    FromBin = stockRow.BinId,
                    LocationBatchId = locationBatchId,
                    BatchId = stockRow.LocationBatch.BatchId,
                    AllocatedQty = (int)take,
                    InventoryStockId = 0,
                    StrategyUsed = nameof(PickStrategy.FEFO),
                    Sequence = sequence,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedDate = nowUtc,
                    IsPickable = true
                };
                await dbContext.InventoryAllocations.AddAsync(inventories, ct);

                sequenceByOrderItem[orderItem.Id] = sequence + 1;
                //AddLocationBatchHoldQty(stockRow.LocationBatch, take, nowUtc);
                remainingByBatchId[locationBatchId] = available - take;

                //add in new table BinInventoryReservation
                var binReservations = new BinInventoryReservation()
                {
                    InventoryAllocation = inventories,
                    LocationInventoryBatchId = locationBatchId,
                    Status = nameof(InventoryStatus.RESERVED),
                    BinId = stockRow.BinId,
                    ReservedQty = (int)take,
                    PickedQty = (int)take,
                    Skuid = orderItem.Sku,
                    OrderItemId = orderItem.Id,
                    CreatedAt = nowUtc,
                    IsSold = false
                };
                await dbContext.BinInventoryReservations.AddAsync(binReservations, ct);

                allocationsCreated++;
                remaining -= take;
            }
        }

        if (allocationsCreated > 0)
        {
            order.OrderStatus = nameof(WmsOrderStatusEnum.INVENTORY_ALLOCATED);
            await dbContext.SaveChangesAsync(ct);
        }

        return new AllocateOrderInventoryResult(
            orderId,
            distinctOrderItems.Count,
            allocationsCreated,
            SkippedAlreadyAllocated: false);
    }

    /// <summary>
    /// Releases inventory allocations for an order by deleting all associated
    /// BinInventoryReservation and InventoryAllocation entries, and reverting the order status to PENDING.
    /// </summary>
    public async Task<ReleaseOrderInventoryResult> ReleaseInventoryAsync(long orderId, CancellationToken ct = default)
    {
        var order = await dbContext.WmsOrders
            .Include(o => o.WmsOrderItems)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
            return new ReleaseOrderInventoryResult(orderId, 0, 0, SkippedNotAllocated: false);

        var distinctOrderItems = order.WmsOrderItems.DistinctBy(i => i.Id).ToList();
        var orderItemIds = distinctOrderItems.Select(i => i.Id).ToList();

        var allocations = await dbContext.InventoryAllocations
            .Where(a => a.OrderItemId != null && orderItemIds.Contains(a.OrderItemId))
            .ToListAsync(ct);

        var allocationIds = allocations.Select(a => a.Id).ToList();

        var reservations = await dbContext.BinInventoryReservations
            .Where(r => (r.OrderItemId != null && orderItemIds.Contains(r.OrderItemId))
                     || allocationIds.Contains(r.InventoryAllocationId))
            .ToListAsync(ct);

        if (allocations.Count == 0 && reservations.Count == 0)
        {
            return new ReleaseOrderInventoryResult(orderId, distinctOrderItems.Count, 0, SkippedNotAllocated: true);
        }

        if (reservations.Count > 0)
        {
            dbContext.BinInventoryReservations.RemoveRange(reservations);
        }

        if (allocations.Count > 0)
        {
            dbContext.InventoryAllocations.RemoveRange(allocations);
        }

        if (string.Equals(order.OrderStatus, nameof(WmsOrderStatusEnum.INVENTORY_ALLOCATED), StringComparison.OrdinalIgnoreCase))
        {
            var nowUtc = DateTime.UtcNow;
            order.OrderStatus = nameof(WmsOrderStatusEnum.PENDING);
            order.UpdatedAt = nowUtc;
            order.UpdatedDate = nowUtc;
        }

        await dbContext.SaveChangesAsync(ct);

        return new ReleaseOrderInventoryResult(
            orderId,
            distinctOrderItems.Count,
            allocations.Count > 0 ? allocations.Count : reservations.Count,
            SkippedNotAllocated: false);
    }

    public async Task<ReleaseOrderInventoryResult> ReleaseInventory(long orderId, CancellationToken ct = default) =>
        await ReleaseInventoryAsync(orderId, ct);

    public async Task<ReleaseOrderInventoryResult> ReleaseAsync(long orderId, CancellationToken ct = default) =>
        await ReleaseInventoryAsync(orderId, ct);

    /// <summary>
    /// Allocates any remaining shortfall for short-pick order items that is not already covered by unlinked allocations.
    /// </summary>
    public async Task<int> AllocateShortfallAsync(long warehouseId, IReadOnlyList<WmsOrderItem> orderItems, CancellationToken ct = default)
    {
        if (orderItems.Count == 0)
            return 0;

        var distinctOrderItems = orderItems.DistinctBy(i => i.Id).ToList();
        var orderItemIds = distinctOrderItems.Select(i => i.Id).ToList();
        // var unlinkedQtyByOrderItem = await dbContext.InventoryAllocations
        //     .Where(a => a.OrderItemId != null
        //                 && orderItemIds.Contains(a.OrderItemId)
        //                 && a.PickListItemId == null
        //                 && a.IsDeleted != true
        //                 && a.IsActive != false)
        //     .GroupBy(a => a.OrderItemId)
        //     .Select(g => new { OrderItemId = g.Key, Qty = g.Sum(a => a.AllocatedQty) })
        //     .ToDictionaryAsync(x => x.OrderItemId, x => x.Qty, ct);

        var skuIds = distinctOrderItems.Select(i => i.Sku).Distinct().ToList();
        var binStockBySku = await LoadTrackedBinStockBySkuWithReservationsAsync(warehouseId, skuIds, ct);

        if (binStockBySku.Count == 0)
            return 0;

        var remainingByBatchId = binStockBySku.Values
            .SelectMany(rows => rows)
            .DistinctBy(row => row.LocationBatch.Id)
            .ToDictionary(
                row => row.LocationBatch.Id,
                row => row.AvailableQty);

        var nowUtc = DateTime.UtcNow;
        var allocationsCreated = 0;
        var sequenceByOrderItem = await dbContext.InventoryAllocations
            .Where(a => a.OrderItemId != null
                        && orderItemIds.Contains(a.OrderItemId)
                        && a.IsDeleted != true)
            .GroupBy(a => a.OrderItemId)
            .Select(g => new { OrderItemId = g.Key, NextSequence = g.Max(a => a.Sequence ?? 0) + 1 })
            .ToDictionaryAsync(x => x.OrderItemId, x => x.NextSequence, ct);
        var processedOrderItemIds = new HashSet<long>();

        foreach (var orderItem in distinctOrderItems)
        {
            if (!processedOrderItemIds.Add(orderItem.Id))
                continue;


            var targetQty = (decimal)((orderItem.Status == "Done" ? (
                            orderItem.PickedQuantity < orderItem.TotalItemQuantity ? (
                                orderItem.TotalItemQuantity - orderItem.PickedQuantity
                            ) : orderItem.PickedQuantity == orderItem.TotalItemQuantity && orderItem.ConfirmedQuantity < orderItem.TotalItemQuantity
                                ? (orderItem.TotalItemQuantity - orderItem.ConfirmedQuantity) : 0
                        ) : 0) ?? 0);

            if (targetQty <= 0)
                continue;

            //unlinkedQtyByOrderItem.TryGetValue(orderItem.Id, out var existingUnlinkedQty);
            //var remaining = targetQty - existingUnlinkedQty;

            var remaining = targetQty;

            if (remaining <= 0)
                continue;

            if (!binStockBySku.TryGetValue(orderItem.Sku, out var binStockRows) || binStockRows.Count == 0)
                continue;

            if (!sequenceByOrderItem.TryGetValue(orderItem.Id, out var sequence))
                sequence = 1;

            foreach (var stockRow in binStockRows)
            {
                if (remaining <= 0)
                    break;

                var locationBatchId = stockRow.LocationBatch.Id;
                if (!remainingByBatchId.TryGetValue(locationBatchId, out var available) || available <= 0)
                    continue;


                var take = Math.Min(available, remaining);
                if (take <= 0)
                    continue;

                var inventory = new InventoryAllocation
                {
                    OrderItemId = orderItem.Id,
                    FromBin = stockRow.BinId,
                    LocationBatchId = locationBatchId,
                    BatchId = stockRow.LocationBatch.BatchId,
                    AllocatedQty = (int)take,
                    InventoryStockId = 0,
                    StrategyUsed = nameof(PickStrategy.FEFO),
                    Sequence = sequence,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedDate = nowUtc,
                    IsPickable = true
                };

                await dbContext.InventoryAllocations.AddAsync(inventory, ct);

                sequence++;
                sequenceByOrderItem[orderItem.Id] = sequence;
                remainingByBatchId[locationBatchId] = available - take;

                allocationsCreated++;
                remaining -= take;

                // Add in table BinInventoryReservation
                var binReservation = new BinInventoryReservation
                {
                    InventoryAllocation = inventory,
                    LocationInventoryBatchId = locationBatchId,
                    Status = nameof(InventoryStatus.RESERVED),
                    BinId = stockRow.BinId,
                    ReservedQty = (int)take,
                    PickedQty = (int)take,
                    Skuid = orderItem.Sku,
                    OrderItemId = orderItem.Id,
                    CreatedAt = nowUtc,
                    IsSold = false
                };
                await dbContext.BinInventoryReservations.AddAsync(binReservation, ct);
            }
        }

        if (allocationsCreated > 0)
            await dbContext.SaveChangesAsync(ct);

        return allocationsCreated;
    }

    private static void AddLocationBatchHoldQty(
        LocationInventoryBatch locationBatch,
        decimal allocatedQty,
        DateTime lastUpdatedUtc)
    {
        locationBatch.HoldQuantity += allocatedQty;
        locationBatch.LastUpdated = lastUpdatedUtc;
    }

    private static DateTime ToUnspecifiedUtcNow() =>
        DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);


    //NOTE: Find the bin and batch for the sku, then sort by isPickable and batch expiry
    // private async Task<Dictionary<long, List<BinStockRow>>> LoadTrackedBinStockBySkuAsync(
    //     long warehouseId,
    //     IReadOnlyCollection<long> skuIds,
    //     CancellationToken ct)
    // {
    //     if (skuIds.Count == 0)
    //         return [];

    //     var stockRows = await dbContext.LocationInventoryBatches
    //         .Include(l => l.Batch)
    //         .Include(l => l.Location)
    //         .Where(l => l.WarehouseId == warehouseId
    //                     && l.SkuId != null && !l.IsDeleted
    //                     && skuIds.Contains(l.SkuId.Value)
    //                     && (l.AvailableQty) > 0
    //                     && l.Batch != null
    //                     && l.Batch.IsActive == true
    //                     && l.Batch.IsDeleted == false
    //                     && l.Location != null
    //                     && l.Location.IsActive == true
    //                     && l.Location.IsDeleted == false
    //                     && l.Location.IsPickable == true)
    //         .ToListAsync(ct);

    //     return stockRows.GroupBy(l => l.SkuId!.Value)
    //         .ToDictionary(
    //             g => g.Key,
    //             g => g
    //                 .Select(l => new BinStockRow(
    //                     l,
    //                     l.LocationId,
    //                     l.Batch?.ExpiryDate,
    //                     l.Location!.IsPickable))
    //                 .OrderByDescending(b => b.IsPickable)
    //                 .ThenBy(b => b.BatchExpiry ?? DateOnly.MaxValue)
    //                 .ToList());
    // }

    private async Task<Dictionary<long, List<BinStockRow>>> LoadTrackedBinStockBySkuWithReservationsAsync(
        long warehouseId, IReadOnlyCollection<long> skuIds, CancellationToken ct)
    {
        if (skuIds.Count == 0)
            return [];

        var skuIdsArray = skuIds.Distinct().ToArray();

        var binStockDetails = await dbContext.Database
            .SqlQuery<BinAvailableStockDetailDto>($"SELECT * FROM get_bin_available_stock_by_sku_list({skuIdsArray}, {warehouseId}, NULL::bigint[], false)")
            .ToListAsync(ct);

        if (binStockDetails.Count == 0)
            return [];

        var availableBinDetails = binStockDetails
            .Where(b => b.AvailableQtyForNewOrders > 0)
            .ToList();

        if (availableBinDetails.Count == 0)
            return [];

        var availableBinIds = availableBinDetails
            .Select(b => b.BinId)
            .Distinct()
            .ToList();

        var availableBatchIds = availableBinDetails
            .Select(b => b.BatchId)
            .Distinct()
            .ToList();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var stockRows = await dbContext.LocationInventoryBatches
            .Include(l => l.Batch)
            .Include(l => l.Location)
            .Where(l => l.WarehouseId == warehouseId
                        && l.SkuId != null
                        && skuIds.Contains(l.SkuId.Value)
                        && availableBinIds.Contains(l.LocationId)
                        && availableBatchIds.Contains(l.Id)
                        && l.Batch != null
                        && l.Batch.IsActive == true
                        && l.Batch.IsDeleted == false
                        && (l.Batch.ExpiryDate == null || l.Batch.ExpiryDate >= today)
                        && l.Location != null
                        && l.Location.IsActive == true
                        && l.Location.IsClusterBased == false
                        && l.Location.IsRoutable == false
                        && l.Location.IsDeleted == false)
            .ToListAsync(ct);

        var uniqeBinIds = stockRows.Select(l => l.LocationId).Distinct().ToList();

        var binsWithSellableZones = await dbContext.Bins.Include(b => b.Aisle).ThenInclude(x => x.Zone)
            .Where(b => uniqeBinIds.Contains(b.Id)
            && b.Aisle.IsActive == true && b.Aisle.IsDeleted == false
            && b.Aisle.Zone.IsActive == true && b.Aisle.Zone.IsDeleted == false
            && b.Aisle.Zone.ZoneType == nameof(ZoneTypeEnum.Sellable))
            .Select(b => b.Id)
            .ToListAsync(ct);

        stockRows = stockRows.Where(l => binsWithSellableZones.Contains(l.LocationId)).ToList();

        var binStockQtyMap = availableBinDetails
            .GroupBy(b => (b.SkuId, b.BinId))
            .ToDictionary(
                g => g.Key,
                g => g.Sum(b => (decimal)b.AvailableQtyForNewOrders));

        var result = new Dictionary<long, List<BinStockRow>>();

        foreach (var skuGroup in stockRows.GroupBy(l => l.SkuId!.Value))
        {
            var skuId = skuGroup.Key;
            var rowsList = new List<BinStockRow>();

            foreach (var binGroup in skuGroup.GroupBy(l => l.LocationId))
            {
                var binId = binGroup.Key;
                if (!binStockQtyMap.TryGetValue((skuId, binId), out var binAvailableForNewOrders) || binAvailableForNewOrders <= 0)
                    continue;

                var remainingBinAvailable = binAvailableForNewOrders;

                var orderedBatches = binGroup
                    .DistinctBy(l => l.Id)
                    .OrderByDescending(l => l.Location!.IsPickable)
                    .ThenBy(l => l.Batch?.ExpiryDate ?? DateOnly.MaxValue);

                foreach (var l in orderedBatches)
                {
                    if (remainingBinAvailable <= 0)
                        break;

                    var batchAvailable = Math.Min(l.AvailableQty, remainingBinAvailable);
                    if (batchAvailable <= 0)
                        continue;

                    remainingBinAvailable -= batchAvailable;

                    rowsList.Add(new BinStockRow(
                        l,
                        l.LocationId,
                        l.Batch?.ExpiryDate,
                        l.Location!.IsPickable,
                        batchAvailable,
                        binAvailableForNewOrders
                    ));
                }
            }

            if (rowsList.Count > 0)
            {
                result[skuId] = [.. rowsList
                    .OrderByDescending(b => b.IsPickable)
                    .ThenBy(b => b.BatchExpiry ?? DateOnly.MaxValue)
                    .ThenBy(b => b.AvailableQtyForNewOrders)];
            }
        }

        return result;
    }
}

public sealed record AllocateOrderInventoryResult(
    long OrderId,
    int OrderItemsProcessed,
    int AllocationsCreated,
    bool SkippedAlreadyAllocated);

public sealed record ReleaseOrderInventoryResult(
    long OrderId,
    int OrderItemsProcessed,
    int AllocationsReleased,
    bool SkippedNotAllocated);
