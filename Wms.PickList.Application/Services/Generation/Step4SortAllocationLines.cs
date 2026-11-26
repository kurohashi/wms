using Wms.Persistence.DbContexts;
using Task = System.Threading.Tasks.Task;

namespace Wms.PickList.Application.Services.Generation;

/// <summary>
/// Step 4: Flatten bin allocations and sort by priority, storage type, skill, and bin sequence.
/// </summary>
internal sealed class Step4SortAllocationLines(WmsDbContextWrite dbContext)
    : PickListGenerationStepBase(dbContext)
{
    protected override Task ExecuteCoreAsync(PickListGenerationContext context, CancellationToken ct)
    {
        context.AllocationLines.Clear();

        foreach (var draft in context.OrderItemDrafts)
        {
            foreach (var binAlloc in draft.BinAllocations)
            {
                context.AllocationLines.Add(new PickListAllocationLineDraft
                {
                    Order = draft.Order,
                    OrderItem = draft.OrderItem,
                    SkuId = draft.SkuId,
                    RequestedQty = draft.RequestedQty,
                    AllocatedQty = binAlloc.AllocatedQty,
                    Priority = draft.Priority,
                    DropLocationId = draft.DropLocationId,
                    StorageTypeId = binAlloc.StorageTypeId,
                    IsFromClusterBin = binAlloc.IsFromClusterBin,
                    InventoryAllocationId = binAlloc.InventoryAllocationId,
                    LocationBatchId = binAlloc.LocationBatchId,
                    BinId = binAlloc.BinId,
                    BatchId = binAlloc.BatchId,
                    InventoryStockId = binAlloc.InventoryStockId,
                    SkillId = binAlloc.SkillId,
                    SkillCapacity = binAlloc.SkillCapacity,
                    BinSequence = binAlloc.BinSequence,
                    ItemStatus = draft.Status
                });
            }
        }

        var sorted = context.AllocationLines
            .OrderBy(l => l.Priority)
            .ThenBy(l => l.IsFromClusterBin ? 0 : 1)
            .ThenBy(l => l.StorageTypeId ?? Guid.Empty)
            .ThenBy(l => l.SkillId ?? long.MaxValue)
            .ThenBy(l => l.SkuId)
            .ThenBy(l => l.BinSequence ?? decimal.MaxValue)
            .ThenBy(l => l.BinId)
            // .ThenBy(l => l.Order.Id)
            // .ThenBy(l => l.OrderItem.Id)
            .ToList();

        context.AllocationLines.Clear();
        context.AllocationLines.AddRange(sorted);

        return Task.CompletedTask;
    }
}
