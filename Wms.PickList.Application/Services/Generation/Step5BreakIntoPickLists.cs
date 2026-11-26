using Microsoft.EntityFrameworkCore;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using PickListEntity = Wms.Domain.Entities.PickList;
using Task = System.Threading.Tasks.Task;

namespace Wms.PickList.Application.Services.Generation;

/// <summary>
/// Step 5: Break sorted allocation lines into pick lists and link existing InventoryAllocation rows to PickListItems.
/// </summary>
internal sealed class Step5BreakIntoPickLists(WmsDbContextWrite dbContext)
    : PickListGenerationStepBase(dbContext)
{
    protected override async Task ExecuteCoreAsync(PickListGenerationContext context, CancellationToken ct)
    {
        if (context.AllocationLines.Count == 0)
            return;

        var pickListDrafts = BuildPickListDrafts(context.AllocationLines, context.MaxUniqueSkus);
        var allocationIds = context.AllocationLines
            .Select(l => l.InventoryAllocationId)
            .Distinct()
            .ToList();

        var allocationsById = await DbContext.InventoryAllocations
            .Include(a => a.BinInventoryReservations)
            .Where(a => allocationIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        foreach (var draft in pickListDrafts)
        {
            if (draft.Lines.Count == 0)
                continue;

            var pickListNumber = await DbContext.Database
                .SqlQuery<string>($"""
                    SELECT GeneratePicklistNumberWH({context.WarehouseId}) AS "Value"
                """)
                .FirstAsync(ct);

            var firstLine = draft.Lines[0];
            var pickList = new PickListEntity
            {
                PickListUuid = Guid.NewGuid(),
                PickListNumber = pickListNumber,
                WarehouseId = context.WarehouseId,
                //StorageTypeId = draft.StorageTypeId,
                DropLocationId = draft.DropLocationId,
                Priority = draft.Priority,
                Status = nameof(PickListStatus.PENDING),
                IsActive = true,
                IsDeleted = false,
                CreatedDate = context.Now,
                CreatedBy = "picklist-generator"
            };

            foreach (var itemGroup in draft.Lines.GroupBy(l => l.OrderItem.Id))
            {
                var firstItemLine = itemGroup.First();
                var pickListItem = new PickListItem
                {
                    PickListItemUuid = Guid.NewGuid(),
                    SkuId = firstItemLine.SkuId,
                    OrderItemId = firstItemLine.OrderItem.Id,
                    RequestedQty = firstItemLine.RequestedQty,
                    AllocatedQty = itemGroup.Sum(l => l.AllocatedQty),
                    PickedQty = 0m,
                    Uom = firstItemLine.OrderItem.Unit,
                    Status = firstItemLine.ItemStatus ?? PickListItemStatus.INVENTORY_ASSIGNED.ToString(),
                    IsActive = true,
                    IsDeleted = false,
                    CreatedDate = context.Now
                };

                var sequence = 1;
                foreach (var line in itemGroup)
                {
                    if (!allocationsById.TryGetValue(line.InventoryAllocationId, out var allocation))
                        continue;

                    if (allocation.PickListItem == null)
                    {
                        // First chunk assigned to a picklist: update original allocation
                        allocation.PickListItem = pickListItem;
                        allocation.AllocatedQty = (int)line.AllocatedQty;
                        allocation.Sequence = sequence++;
                        allocation.ModifiedDate = context.Now;
                        pickListItem.InventoryAllocations.Add(allocation);

                        var reservation = allocation.BinInventoryReservations.FirstOrDefault();
                        if (reservation != null)
                        {
                            reservation.ReservedQty = (int)line.AllocatedQty;
                            reservation.PickedQty = (int)line.AllocatedQty;
                            reservation.UpdatedAt = context.Now;
                        }
                    }
                    else
                    {
                        // Split chunk: create a new InventoryAllocation and BinInventoryReservation row
                        var splitAllocation = new InventoryAllocation
                        {
                            OrderItemId = allocation.OrderItemId,
                            FromBin = allocation.FromBin,
                            LocationBatchId = allocation.LocationBatchId,
                            BatchId = allocation.BatchId,
                            AllocatedQty = (int)line.AllocatedQty,
                            InventoryStockId = allocation.InventoryStockId,
                            StrategyUsed = allocation.StrategyUsed,
                            Sequence = sequence++,
                            IsActive = true,
                            IsDeleted = false,
                            CreatedDate = context.Now,
                            IsPickable = allocation.IsPickable,
                            PickListItem = pickListItem
                        };
                        DbContext.InventoryAllocations.Add(splitAllocation);
                        pickListItem.InventoryAllocations.Add(splitAllocation);

                        var originalRes = allocation.BinInventoryReservations.FirstOrDefault();
                        if (originalRes != null)
                        {
                            var splitReservation = new BinInventoryReservation
                            {
                                InventoryAllocation = splitAllocation,
                                LocationInventoryBatchId = originalRes.LocationInventoryBatchId,
                                Status = nameof(InventoryStatus.RESERVED),
                                BinId = originalRes.BinId,
                                ReservedQty = (int)line.AllocatedQty,
                                PickedQty = (int)line.AllocatedQty,
                                Skuid = originalRes.Skuid,
                                OrderItemId = originalRes.OrderItemId,
                                CreatedAt = context.Now,
                                IsSold = false
                            };
                            DbContext.BinInventoryReservations.Add(splitReservation);
                        }
                    }
                }

                pickList.PickListItems.Add(pickListItem);
            }

            DbContext.PickLists.Add(pickList);
            context.PickLists.Add(pickList);
            context.Counters.PickListsCreated++;
        }

        if (context.ShortPickOrderItems is null && context.PickLists.Count > 0)
        {
            var allocatedOrderItemIds = context.PickLists
                .SelectMany(p => p.PickListItems)
                .Select(i => i.OrderItemId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToHashSet();

            var isClusterGeneration = pickListDrafts.Any(d => d.IsFromClusterBin)
                || context.AllocationLines.Any(l => l.DropLocationId.HasValue && !l.IsFromClusterBin && context.OrderItemDrafts.Any(d => d.IsBulkPick && d.DropLocationId == l.DropLocationId));

            foreach (var order in context.Orders)
            {
                var allItemsAllocated = order.WmsOrderItems.All(i => allocatedOrderItemIds.Contains(i.Id));
                var anyItemAllocated = order.WmsOrderItems.Any(i => allocatedOrderItemIds.Contains(i.Id));

                if (!anyItemAllocated)
                    continue;

                order.IsPicklistCreated = true;
                order.UpdatedAt = context.Now;
                // For full generation or when all items are allocated, mark created.
                // If some items were skipped (e.g. non-bulk items during cluster run), do not mark until route generation.
                if (allItemsAllocated || !isClusterGeneration)
                {
                    context.Counters.OrdersMarkedGenerated++;
                }
            }
        }
    }

    private static List<PickListDraft> BuildPickListDrafts(IReadOnlyList<PickListAllocationLineDraft> sortedLines, int maxUniqueSkus)
    {
        var drafts = new List<PickListDraft>();
        PickListDraft? current = null;
        var currentUniqueSkus = new HashSet<long>();

        long? currentDrop = null;
        Guid? currentStorage = null;
        int? currentPriority = null;
        long? currentSkill = null;
        bool? currentIsFromCluster = null;

        var effectiveMaxSkus = maxUniqueSkus > 0 ? maxUniqueSkus : 30;

        foreach (var line in sortedLines)
        {
            var remainingQty = line.AllocatedQty;

            // #PicklistWeight_New: Get item's unit weight (defaults to 0 if missing)
            var unitWeight = line.UnitWeight;

            while (remainingQty > 0)
            {
                var keyChanged = current is null
                                 || line.DropLocationId != currentDrop
                                 || line.StorageTypeId != currentStorage
                                 || line.Priority != currentPriority
                                 || line.SkillId != currentSkill
                                 || line.IsFromClusterBin != currentIsFromCluster;

                var skuLimitExceeded = current is not null
                                      && !currentUniqueSkus.Contains(line.SkuId)
                                      && currentUniqueSkus.Count >= effectiveMaxSkus;

                var capacity = line.SkillCapacity;

                // #PicklistWeight_Old: Total quantity check
                // var currentTotalQty = current?.Lines.Sum(l => l.AllocatedQty) ?? 0;

                // #PicklistWeight_New: Total accumulated weight check
                var currentTotalWeight = current?.Lines.Sum(l => l.TotalWeight) ?? 0m;

                var needNewPickList = keyChanged || skuLimitExceeded;

                if (needNewPickList)
                {
                    current = new PickListDraft
                    {
                        DropLocationId = line.DropLocationId,
                        StorageTypeId = line.StorageTypeId,
                        Priority = line.Priority,
                        SkillId = line.SkillId,
                        IsFromClusterBin = line.IsFromClusterBin
                    };
                    drafts.Add(current);
                    currentUniqueSkus.Clear();
                    currentDrop = line.DropLocationId;
                    currentStorage = line.StorageTypeId;
                    currentPriority = line.Priority;
                    currentSkill = line.SkillId;
                    currentIsFromCluster = line.IsFromClusterBin;

                    // #PicklistWeight_Old:
                    // currentTotalQty = 0;

                    // #PicklistWeight_New:
                    currentTotalWeight = 0m;
                }

                decimal take;

                // #PicklistWeight_Old: Capacity check based on total quantity
                /*
                if (capacity > 0)
                {
                    var remainingCapacity = Math.Max(0, capacity - currentTotalQty);
                    if (remainingCapacity == 0)
                    {
                        current = new PickListDraft
                        {
                            DropLocationId = line.DropLocationId,
                            StorageTypeId = line.StorageTypeId,
                            Priority = line.Priority,
                            SkillId = line.SkillId,
                            IsFromClusterBin = line.IsFromClusterBin
                        };
                        drafts.Add(current);
                        currentUniqueSkus.Clear();
                        currentDrop = line.DropLocationId;
                        currentStorage = line.StorageTypeId;
                        currentPriority = line.Priority;
                        currentSkill = line.SkillId;
                        currentIsFromCluster = line.IsFromClusterBin;
                        remainingCapacity = capacity;
                    }

                    take = Math.Min(remainingQty, remainingCapacity);
                }
                else
                {
                    take = remainingQty;
                }
                */

                // #PicklistWeight_New: Capacity check based on total product weight (unitWeight = 0 skips weight constraint)
                if (capacity > 0 && unitWeight > 0)
                {
                    var remainingWeightCapacity = Math.Max(0m, capacity - currentTotalWeight);
                    var maxUnitsFit = Math.Floor(remainingWeightCapacity / unitWeight);

                    if (maxUnitsFit == 0 && current is not null && current.Lines.Count > 0)
                    {
                        current = new PickListDraft
                        {
                            DropLocationId = line.DropLocationId,
                            StorageTypeId = line.StorageTypeId,
                            Priority = line.Priority,
                            SkillId = line.SkillId,
                            IsFromClusterBin = line.IsFromClusterBin
                        };
                        drafts.Add(current);
                        currentUniqueSkus.Clear();
                        currentDrop = line.DropLocationId;
                        currentStorage = line.StorageTypeId;
                        currentPriority = line.Priority;
                        currentSkill = line.SkillId;
                        currentIsFromCluster = line.IsFromClusterBin;

                        remainingWeightCapacity = capacity;
                        maxUnitsFit = Math.Floor(remainingWeightCapacity / unitWeight);
                    }

                    var allowedUnits = Math.Max(1m, maxUnitsFit);
                    take = Math.Min(remainingQty, allowedUnits);
                }
                else
                {
                    take = remainingQty;
                }

                currentUniqueSkus.Add(line.SkuId);

                var chunkLine = new PickListAllocationLineDraft
                {
                    Order = line.Order,
                    OrderItem = line.OrderItem,
                    SkuId = line.SkuId,
                    RequestedQty = line.RequestedQty,
                    AllocatedQty = take,
                    Priority = line.Priority,
                    DropLocationId = line.DropLocationId,
                    StorageTypeId = line.StorageTypeId,
                    IsFromClusterBin = line.IsFromClusterBin,
                    InventoryAllocationId = line.InventoryAllocationId,
                    LocationBatchId = line.LocationBatchId,
                    BinId = line.BinId,
                    BatchId = line.BatchId,
                    InventoryStockId = line.InventoryStockId,
                    SkillId = line.SkillId,
                    SkillCapacity = line.SkillCapacity,
                    BinSequence = line.BinSequence,
                    ItemStatus = line.ItemStatus
                };
                current!.Lines.Add(chunkLine);

                remainingQty -= take;
            }
        }

        return drafts;
    }
}
