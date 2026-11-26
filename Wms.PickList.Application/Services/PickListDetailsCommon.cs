using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Domain.Entities;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Services;

public static class PickListDetailsCommon
{
    public static async Task<PickListDetailsDto> GetPickListDetailsAsync(WmsDbContextWrite dbContext, ICurrentUser currentUser, Guid pickListUuid, CancellationToken ct = default)
    {
        long warehouseId = currentUser.WarehouseId ?? 0;
        if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

        var picklist = await dbContext.PickLists.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PickListUuid == pickListUuid && x.WarehouseId == warehouseId, ct)
            ?? throw new BusinessRuleException("Pick list not found.");

        // 1. Fetch PickList items joined with order items
        var pickListItemsData = await dbContext.PickListItems.AsNoTracking()
            .Where(pi => pi.PickListId == picklist.Id && pi.IsActive == true && pi.IsDeleted == false)
            .Join(dbContext.WmsOrderItems, pi => pi.OrderItemId, oi => oi.Id, (pi, oi) => new
            {
                pi.Id,
                pi.PickListItemUuid,
                pi.SkuId,
                pi.AllocatedQty,
                pi.PickedQty,
                pi.Status,
                pi.Uom,
                ProductName = oi.Name,
                oi.TotalItemPrice
            })
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        var skuIds = pickListItemsData
            .Select(x => x.SkuId)
            .Distinct()
            .ToList();

        var productsInfo = (await dbContext.Products
            .AsNoTracking()
            .Where(p => skuIds.Contains(p.Sku))
            .Select(p => new
            {
                p.Sku,
                p.ProductImageUrl,
                p.IsScanMandatory
            })
            .ToListAsync(ct))
            .DistinctBy(p => p.Sku)
            .ToDictionary(p => p.Sku, p => p);

        var productEans = (await dbContext.ProductEans
            .AsNoTracking()
            .Where(pe => skuIds.Contains(pe.SkuId) && pe.IsActive && !pe.IsDeleted)
            .OrderByDescending(pe => pe.IsPrimary)
            .ThenBy(pe => pe.Id)
            .Select(pe => new
            {
                pe.SkuId,
                pe.Ean
            })
            .ToListAsync(ct))
            .GroupBy(pe => pe.SkuId)
            .ToDictionary(g => g.Key, g => g.First().Ean);

        // 2. Fetch inventory allocations at once for all pick list items
        var pickListItemIds = pickListItemsData.Select(x => x.Id).ToList();

        var allocations = await dbContext.InventoryAllocations.AsNoTracking()
            .Where(a => a.PickListItemId.HasValue && pickListItemIds.Contains(a.PickListItemId.Value) && a.IsActive == true && a.IsDeleted == false)
            .Select(a => new
            {
                a.Id,
                PickListItemId = a.PickListItemId!.Value,
                ZoneCode = a.FromBinNavigation.Aisle != null ? a.FromBinNavigation.Aisle.Zone.Code : null,
                BinBatch = new BinBatchDto(
                    a.FromBinNavigation.BinUuid,
                    a.FromBinNavigation.Code,
                    a.Batch != null ? a.Batch.ExpiryDate : null,
                    a.LocationBatchId ?? 0,
                    a.PickedQty ?? 0,
                    a.AllocatedQty,
                    a.FromBinNavigation.IsRoutable,
                    a.FromBinNavigation.IsClusterBased,
                    a.FromBinNavigation.IsLocked,
                    a.FromBinNavigation.Sequence,
                    0,
                    0,
                    a.FromBinNavigation.Position,
                    a.FromBinNavigation.Aisle!.Zone.Code,
                    a.FromBinNavigation.Aisle.Code,
                    a.FromBinNavigation.Rack,
                    a.FromBinNavigation.Bay,
                    a.FromBinNavigation.Level,
                    a.FromBinNavigation.IsScanMandatory
                )
            }).ToListAsync(ct);

        var zoneCode = allocations.Select(a => a.ZoneCode).FirstOrDefault(z => !string.IsNullOrEmpty(z));

        var allocationsByItemId = allocations.GroupBy(a => a.PickListItemId).ToDictionary(g => g.Key, g => g.ToList());

        decimal totalAmount = pickListItemsData.Sum(x => x.TotalItemPrice);

        var rawProductsResponse = pickListItemsData
            .Select(item =>
            {
                var itemAllocations = allocationsByItemId.GetValueOrDefault(item.Id) ?? [];
                long inventoryAllocationId = itemAllocations.FirstOrDefault()?.Id ?? 0L;
                var binDetailsList = itemAllocations.Select(a => a.BinBatch).ToList();
                var sortedBinDetails = binDetailsList.OrderBy(x => x.ExpiryDate).ThenByDescending(x => x.Sequence).ToList();

                bool suggestedBins = sortedBinDetails.Count > 0 &&
                                    !sortedBinDetails.All(c => c.ClusterBased) &&
                                    !sortedBinDetails.All(c => c.RouteBased);

                int allocatedQty = (int)(item.AllocatedQty ?? 0m);
                int pickedQty = (int)(item.PickedQty ?? 0m);
                int shortQty = item.Status == "SHORT_PICK" ? (allocatedQty - pickedQty) : 0;

                var productInfo = productsInfo.GetValueOrDefault(item.SkuId);
                var productEan = productEans.GetValueOrDefault(item.SkuId);

                return new PickListProductDto(
                    PickListItemUuid: item.PickListItemUuid,
                    Sku: item.SkuId,
                    ProductName: item.ProductName,
                    Quantity: allocatedQty,
                    Uom: item.Uom ?? string.Empty,
                    PickedQty: pickedQty,
                    ShortQty: shortQty,
                    InventoryAllocationId: inventoryAllocationId,
                    SuggestedBins: suggestedBins,
                    DamageQty: 0,
                    BinDetails: sortedBinDetails,
                    ProductImage: productInfo?.ProductImageUrl,
                    IsScanMandatory: productInfo?.IsScanMandatory ?? false,
                    ProductEan: productEan
                );
            }).ToList();

        // Centralized Grouping by SKU and Bin Details
        var productsResponse = rawProductsResponse
            .GroupBy(p => new
            {
                p.Sku
                //,BinKey = string.Join("|", p.BinDetails.OrderBy(b => b.BinCode).Select(b => $"{b.BinCode}:{b.BinUuid}"))
            })
            .Select(group =>
            {
                var first = group.First();
                var mergedBinDetails = group
                    .SelectMany(p => p.BinDetails)
                    .GroupBy(b => new { b.BinUuid, b.BinCode, b.RouteBased, b.ClusterBased })
                    .Select(bg =>
                    {
                        var firstBin = bg.First();
                        return new BinBatchDto(
                            BinUuid: bg.Key.BinUuid,
                            BinCode: bg.Key.BinCode,
                            ExpiryDate: bg.Min(b => b.ExpiryDate),
                            LocationInventoryBatchId: bg.Where(b => b.ExpiryDate == bg.Min(b => b.ExpiryDate)).Select(b => b.LocationInventoryBatchId).FirstOrDefault(),
                            PickedQty: bg.Sum(b => b.PickedQty),
                            AvailableQty: bg.Sum(b => b.AvailableQty),
                            RouteBased: bg.Key.RouteBased,
                            ClusterBased: bg.Key.ClusterBased,
                            IsLocked: bg.All(b => b.IsLocked),
                            Sequence: bg.Max(b => b.Sequence),
                            StockOnHand: 0,
                            DamageQty: 0,
                            Position: firstBin.Position,
                            Zone: firstBin.Zone,
                            Aisle: firstBin.Aisle,
                            Rack: firstBin.Rack,
                            Bay: firstBin.Bay,
                            Level: firstBin.Level,
                            IsScanMandatory: firstBin.IsScanMandatory
                        );
                    })
                    .OrderBy(b => b.ExpiryDate).ThenByDescending(b => b.Sequence)
                    .ToList();

                return new PickListProductDto(
                    PickListItemUuid: first.PickListItemUuid,
                    Sku: first.Sku,
                    ProductName: first.ProductName,
                    Quantity: group.Sum(p => p.Quantity),
                    Uom: first.Uom,
                    PickedQty: group.Sum(p => p.PickedQty),
                    ShortQty: group.Sum(p => p.Quantity) - group.Sum(p => p.PickedQty),
                    InventoryAllocationId: first.InventoryAllocationId,
                    SuggestedBins: first.SuggestedBins,
                    DamageQty: 0,
                    BinDetails: mergedBinDetails,
                    ProductImage: first.ProductImage,
                    IsScanMandatory: first.IsScanMandatory,
                    ProductEan: first.ProductEan
                );
            })
            .ToList();

        // Assigned worker
        string? workerName = null;
        if (picklist.AssignedWorkerId.HasValue)
        {
            workerName = await dbContext.Workers.AsNoTracking()
                .Where(x => x.Id == picklist.AssignedWorkerId.Value && !x.IsDeleted && x.IsActive && x.WarehouseId == warehouseId)
                .Select(x => x.Name).FirstOrDefaultAsync(ct);
        }

        // Drop zone
        string? dropZoneLocation = null;
        string? dropLocationUuid = null;
        bool scanMandatory = false;
        if (picklist.DropLocationId.HasValue)
        {
            var dropBin = await dbContext.Bins
                .AsNoTracking()
                .Where(x => x.Id == picklist.DropLocationId.Value)
                .Select(x => new { x.Code, x.BinUuid, x.IsScanMandatory })
                .FirstOrDefaultAsync(ct);

            if (dropBin != null)
            {
                dropZoneLocation = dropBin.Code;
                dropLocationUuid = dropBin.BinUuid.ToString();
                scanMandatory = dropBin.IsScanMandatory;
            }
        }

        // Fetch Orders associated with this Picklist
        var orders = await dbContext.PickListItems.AsNoTracking()
            .Where(pi => pi.PickListId == picklist.Id && pi.IsActive == true && pi.IsDeleted == false)
            .Join(dbContext.WmsOrderItems, pi => pi.OrderItemId, oi => oi.Id, (pi, oi) => oi)
            .Join(dbContext.WmsOrders, oi => oi.OrderId, o => o.Id, (oi, o) => new PickListOrderDto(o.OrderUuid, o.UniqueOrderId))
            .Distinct()
            .ToListAsync(ct);

        var skuIdsArray = pickListItemsData.Select(p => p.SkuId).ToArray();

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

        for (var j = 0; j < productsResponse.Count; j++)
        {
            var product = productsResponse[j];
            var binDetails = product.BinDetails;
            for (int i = 0; i < binDetails.Count; i++)
            {
                var bin = binDetails[i];
                var stockDetail = binStockDetails.FirstOrDefault(b => b.SkuId == product.Sku && b.BinUuid == bin.BinUuid && b.BatchId == bin.LocationInventoryBatchId);
                if (stockDetail != null)
                    binDetails[i] = bin with { StockOnHand = stockDetail.StockOnHand };

                var damageDetail = damageDetails.FirstOrDefault(b => b.SkuId == product.Sku && b.BinUuid == bin.BinUuid);
                if (damageDetail != null)
                    binDetails[i] = bin with { DamageQty = damageDetail.TotalDamageQty };
            }
            productsResponse[j] = product with { DamageQty = binDetails.Sum(b => b.DamageQty) };
        }

        return new PickListDetailsDto(
            PickListUuid: pickListUuid,
            PickListNumber: picklist.PickListNumber ?? string.Empty,
            TotalAmount: (int)totalAmount,
            TotalItems: productsResponse.GroupBy(p => p.Sku).Count(),
            Status: picklist.Status,
            CreatedAt: picklist.CreatedDate,
            DropZoneLocation: dropZoneLocation ?? string.Empty,
            DropLocation: dropZoneLocation ?? string.Empty,
            DropLocationUuid: dropLocationUuid ?? string.Empty,
            DropBinIsScanMandatory: scanMandatory,
            ZoneCode: zoneCode,
            assigneeName: workerName,
            Priority: picklist.Priority,
            Orders: orders,
            Products: [.. productsResponse.OrderBy(p => p.BinDetails.MaxBy(x => x.Sequence)?.Sequence ?? 0)]
        );
    }
}
