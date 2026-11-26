using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Query;

public sealed class GetSuggestedBinsQuery(Guid picklistItemUuid) : IQuery<SuggestedBinsResponseDto>
{
    public Guid PicklistItemUuid { get; } = picklistItemUuid;

    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser) : IQueryHandler<GetSuggestedBinsQuery, SuggestedBinsResponseDto>
    {
        public async Task<SuggestedBinsResponseDto> HandleAsync(GetSuggestedBinsQuery query, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0;
            if (warehouseId == 0)
                throw new BusinessRuleException("User is not associated with any warehouse.");

            if (query.PicklistItemUuid == Guid.Empty)
                throw new BusinessRuleException("picklistItemUuid is required.");

            var picklistItem = await dbContext.PickListItems
                .Where(x => x.PickListItemUuid == query.PicklistItemUuid)
                .Select(x => new { x.Id, x.SkuId, x.PickListId })
                .FirstOrDefaultAsync(ct)
                ?? throw new BusinessRuleException($"PickListItem with UUID {query.PicklistItemUuid} not found.");

            var skuId = picklistItem.SkuId;

            var allocatedBinIds = await dbContext.InventoryAllocations
                .AsNoTracking()
                .Where(x => x.PickListItemId == picklistItem.Id && x.FromBin != null)
                .Select(x => x.FromBin!.Value)
                .Distinct()
                .ToListAsync(ct);

            var totalRequiredQtyToReserve = await dbContext.PickListItems.AsNoTracking()
                .Where(x => x.SkuId == skuId && x.PickListId == picklistItem.PickListId && x.IsActive == true && x.IsDeleted == false)
                .SumAsync(x => (int?)x.AllocatedQty ?? 0, ct);

            var skuIdsArray = new long[] { skuId };

            var binStockDetails = await dbContext.Database
                .SqlQuery<BinAvailableStockDetailDto>($"SELECT * FROM get_bin_available_stock_by_sku_list({skuIdsArray}, {warehouseId}, NULL::bigint[], false)")
                .ToListAsync(ct);

            var availableBinIds = binStockDetails
                .Where(b => b.AvailableQtyForNewOrders > 0 && b.AvailableQtyForNewOrders >= totalRequiredQtyToReserve)
                .Select(b => b.BinId)
                .Distinct()
                .ToList();
           
            var availableBatchIds = binStockDetails
                .Where(b => b.AvailableQtyForNewOrders > 0 && b.AvailableQtyForNewOrders >= totalRequiredQtyToReserve)
                .Select(b => b.BatchId)
                .Distinct()
                .ToList();

            if (availableBinIds.Count == 0)
            {
                return new SuggestedBinsResponseDto(skuId, new List<SuggestedBinDto>());
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            var bins = await dbContext.LocationInventoryBatches.AsNoTracking()
                .Include(x => x.Batch)
                .Include(x => x.Location)
                .Where(l =>
                    l.WarehouseId == warehouseId
                    && l.SkuId == skuId
                    && availableBinIds.Contains(l.LocationId)
                    && availableBatchIds.Contains(l.Id)
                    && l.BatchId != null
                    && l.Batch != null
                    && l.Batch.IsActive == true
                    && l.SkuId == l.Batch.SkuId
                    && l.Batch.IsDeleted == false
                    && (l.Batch.ExpiryDate == null || l.Batch.ExpiryDate >= today)
                    && l.Location != null
                    && l.Location.IsActive == true
                    && l.Location.IsDeleted == false
                    && !allocatedBinIds.Contains(l.LocationId))
                .OrderByDescending(l => l.Location!.IsPickable)
                .ThenBy(l => l.Batch!.ExpiryDate ?? DateOnly.MaxValue)
                .Select(l => new SuggestedBinDto(
                    l.SkuId ?? skuId,
                    l.Location!.BinUuid,
                    l.LocationId,
                    l.Location!.Code,
                    l.BatchId!.Value,
                    l.Batch!.BatchUuid,
                    l.Batch!.BatchNumber,
                    l.Location.IsLocked,
                    l.Batch!.ExpiryDate != null
                        ? l.Batch!.ExpiryDate!.Value.ToString("yyyy-MM-dd")
                        : null))
                .ToListAsync(ct);

            return new SuggestedBinsResponseDto(skuId, bins);
        }
    }
}
