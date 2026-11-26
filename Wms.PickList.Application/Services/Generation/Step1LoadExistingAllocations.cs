using Microsoft.EntityFrameworkCore;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Helper;
using Wms.Work.Application.Common.Helpers;
using Task = System.Threading.Tasks.Task;

namespace Wms.PickList.Application.Services.Generation;

/// <summary>
/// Step 1: Load persisted order-item inventory allocations and build in-memory drafts for pick list generation.
/// For short-pick regeneration, allocates any remaining shortfall before loading unlinked rows.
/// </summary>
internal sealed class Step1LoadExistingAllocations(WmsDbContextWrite dbContext, OrderInventoryAllocationService allocationService) : PickListGenerationStepBase(dbContext)
{
    protected override async Task ExecuteCoreAsync(PickListGenerationContext context, CancellationToken ct)
    {
        BuildOrderItemDrafts(context);

        if (context.OrderItemDrafts.Count == 0)
            return;

        var finalZoneCode = await dbContext.GetFinalZone(ct);

        var orderItemIds = context.OrderItemDrafts.Select(d => d.OrderItem.Id).ToList();

        if (context.ShortPickOrderItems is null)
        {
            await EnsureDropZoneAllocationsForCompletedPicklistsAsync(orderItemIds, ct);
        }

        var allocationsQuery = DbContext.InventoryAllocations
            .AsNoTracking()
            .Include(a => a.Batch)
            .Include(a => a.FromBinNavigation)
                .ThenInclude(b => b.Aisle)
                    .ThenInclude(z => z.Zone)
            .Where(a => a.OrderItemId != null
                        && orderItemIds.Contains(a.OrderItemId)
                        && a.PickListItemId == null
                        && a.IsLeftover != true
                        && a.IsDeleted != true
                        && a.IsActive != false);

        if (context.ShortPickOrderItems is not null)
        {
            // For short-pick, only pick shortfall from non-routable warehouse storage/picking bins.
            // Do NOT pick from drop locations or routable bins (which are for already picked/dropped items).
            allocationsQuery = allocationsQuery.Where(a =>
                (a.FromBinNavigation != null && a.FromBinNavigation.IsRoutable == false)
                || (a.FromBinNavigation == null));
        }
        else
        {
            allocationsQuery = allocationsQuery.Where(a =>
                !(a.FromBinNavigation.Aisle.Zone.Code == finalZoneCode && a.FromBinNavigation.IsRoutable == true));
        }

        var allocations = await allocationsQuery
            .OrderBy(a => a.OrderItemId)
            .ThenBy(a => a.Sequence)
            .ToListAsync(ct);

        var binIds = allocations
            .Select(a => a.FromBin)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var skuIds = context.OrderItemDrafts.Select(d => d.SkuId).Distinct().ToList();
        var products = await DbContext.Products
            .AsNoTracking()
            .Where(p => skuIds.Contains(p.Sku))
            .Select(p => new {p.Sku, p.IsBulkPick })
            .ToListAsync(ct);

        var bulkPickMap = new Dictionary<long, bool>();
        foreach (var p in products)
        {
            if (p.Sku > 0)
                bulkPickMap[p.Sku] = p.IsBulkPick;
        }

        var skillsByBin = await LoadSkillsByBinAsync(binIds, ct);

        foreach (var draft in context.OrderItemDrafts)
        {
            draft.IsBulkPick = bulkPickMap.TryGetValue(draft.SkuId, out var isBulk) && isBulk;

            var itemAllocations = allocations
                .Where(a => a.OrderItemId == draft.OrderItem.Id)
                .ToList();

            if (itemAllocations.Count == 0)
            {
                MarkUnallocated(draft, context);
                continue;
            }

            foreach (var allocation in itemAllocations)
            {
                //var binId = allocation.FromBin;
                //skillsByBin.TryGetValue(binId, out var skill);
                //var pickBin = allocation.FromBin is > 0
                //    ? allocation.FromBinNavigation ?? allocation.LocationBatch?.Location
                //    : allocation.LocationBatch?.Location;

                //draft.BinAllocations.Add(new BinAllocationDraft
                //{
                //    InventoryAllocationId = allocation.Id,
                //    LocationBatchId = allocation.LocationBatchId ?? 0,
                //    BinId = binId,
                //    BatchId = allocation.BatchId ?? allocation.LocationBatch?.BatchId ?? 0,
                //    InventoryStockId = allocation.InventoryStockId,
                //    AllocatedQty = allocation.AllocatedQty,
                //    BatchExpiry = allocation.Batch?.ExpiryDate ?? allocation.LocationBatch?.Batch?.ExpiryDate,
                //    IsPickable = pickBin?.IsPickable ?? false,
                //    IsRoutable = pickBin?.IsRoutable ?? false,
                //    IsFromClusterBin = pickBin?.IsClusterBased ?? false,
                //    BinStorageType = pickBin?.StorageType,
                //    StorageTypeId = pickBin?.StorageTypeUuid,
                //    SkillId = skill.SkillId == 0 ? null : skill.SkillId,
                //    SkillCapacity = skill.Capacity,
                //    BinSequence = pickBin?.Sequence
                //});
            }

            var allocatedQty = itemAllocations.Sum(a => a.AllocatedQty);
            if (allocatedQty == 0)
            {
                MarkUnallocated(draft, context);
                continue;
            }

            if (context.ShortPickOrderItems is not null && draft.RequestedQty > allocatedQty)
            {
                draft.RequestedQty = allocatedQty;
            }

            if (allocatedQty < draft.RequestedQty)
            {
                draft.Status = PickListItemStatus.SHORT_PICK.ToString();
                context.Counters.ItemsPartiallyAllocated++;
                continue;
            }

            draft.Status = nameof(PickListItemStatus.INVENTORY_ASSIGNED);
            context.Counters.ItemsFullyAllocated++;
        }
    }

    private static void BuildOrderItemDrafts(PickListGenerationContext context)
    {
        foreach (var order in context.Orders)
        {
            var orderItems = context.SortOrderItems is not null
                ? context.SortOrderItems.Where(i => i.OrderId == order.Id).DistinctBy(i => i.Id).ToList()
                : context.ShortPickOrderItems is not null
                    ? context.ShortPickOrderItems.Where(i => i.OrderId == order.Id).DistinctBy(i => i.Id).ToList()
                    : order.WmsOrderItems.DistinctBy(i => i.Id).ToList();

            foreach (var orderItem in orderItems)
            {
                decimal requestedQty = orderItem.TotalItemQuantity;

                if (context.ShortPickOrderItems is not null)
                {
                    if (orderItem.ConfirmedQuantity.HasValue && orderItem.ConfirmedQuantity.Value < orderItem.TotalItemQuantity)
                    {
                        var shortfall = orderItem.TotalItemQuantity - orderItem.ConfirmedQuantity.Value;
                        if (shortfall > 0)
                            requestedQty = shortfall;
                    }
                    else if (orderItem.PickedQuantity.HasValue && orderItem.PickedQuantity.Value < orderItem.TotalItemQuantity)
                    {
                        var shortfall = orderItem.TotalItemQuantity - orderItem.PickedQuantity.Value;
                        if (shortfall > 0)
                            requestedQty = shortfall;
                    }
                }

                context.OrderItemDrafts.Add(new OrderItemAllocationDraft
                {
                    Order = order,
                    OrderItem = orderItem,
                    SkuId = orderItem.Sku,
                    RequestedQty = requestedQty
                });
            }
        }
    }

    private async Task<Dictionary<long, (long SkillId, decimal Capacity)>> LoadSkillsByBinAsync(
        IReadOnlyCollection<long> binIds,
        CancellationToken ct)
    {
        if (binIds.Count == 0)
            return [];

        var mappings = await DbContext.BinSkillMappings
            .AsNoTracking()
            .Include(m => m.CertificationMaster)
            .Where(m => binIds.Contains(m.BinId)
                        && m.IsActive
                        && !m.IsDeleted
                        && m.CertificationMaster.IsActive
                        && !m.CertificationMaster.IsDeleted)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        return mappings
            .GroupBy(m => m.BinId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var skill = g.First().CertificationMaster;
                    return (skill.Id, skill.Capacity);
                });
    }

    private async Task EnsureDropZoneAllocationsForCompletedPicklistsAsync(
        List<long> orderItemIds, CancellationToken ct)
    {
        var completedPicklists = await DbContext.PickLists
            .Include(p => p.PickListItems)
            .Include(p => p.DropLocation)
            .Where(p => p.IsActive == true
                        && p.IsDeleted != true
                        && p.Status == nameof(PickListStatus.COMPLETED)
                        && p.DropLocationId != null
                        && p.PickListItems.Any(pi => pi.OrderItemId.HasValue && orderItemIds.Contains(pi.OrderItemId.Value)))
            .ToListAsync(ct);

        if (completedPicklists.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var pl in completedPicklists)
        {
            if (pl.DropLocation == null || pl.DropLocation.IsRoutable)
                continue;

            var plItemIds = pl.PickListItems.Select(i => i.Id).ToList();
            var plOrderItemIds = pl.PickListItems
                .Where(i => i.OrderItemId.HasValue && orderItemIds.Contains(i.OrderItemId.Value))
                .Select(i => i.OrderItemId!.Value)
                .Distinct()
                .ToList();

            var existingUnlinkedDropAllocOrderItemIds = await DbContext.InventoryAllocations
                .Where(a => plOrderItemIds.Contains(a.OrderItemId)
                            && a.FromBin == pl.DropLocationId
                            && a.PickListItemId == null
                            && a.IsActive != false
                            && a.IsDeleted != true)
                .Select(a => a.OrderItemId)
                .Distinct()
                .ToListAsync(ct);

            var missingOrderItemIds = plOrderItemIds.Except(existingUnlinkedDropAllocOrderItemIds).ToList();
            if (missingOrderItemIds.Count == 0)
                continue;

            var plAllocations = await DbContext.InventoryAllocations
                .Where(a => a.PickListItemId != null && plItemIds.Contains(a.PickListItemId.Value) && a.IsDeleted != true)
                .ToListAsync(ct);

            if (plAllocations.Count > 0)
            {
                //await PicklistHelper.CreateDropZoneAllocationsAsync(
                //    DbContext,
                //    pl,
                //    plAllocations,
                //    nameof(PickListStatus.COMPLETED),
                //    now,
                //    ct);
            }
        }
    }

    private static void MarkUnallocated(OrderItemAllocationDraft draft, PickListGenerationContext context)
    {
        draft.Status = nameof(PickListItemStatus.INVENTORY_INSUFFICIENT);
        context.Counters.ItemsUnallocated++;
    }
}
