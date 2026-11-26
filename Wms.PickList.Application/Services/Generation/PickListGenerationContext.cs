using Wms.Domain.Entities;
using Wms.PickList.Application.Command;
using PickListEntity = Wms.Domain.Entities.PickList;

namespace Wms.PickList.Application.Services.Generation;

internal sealed class PickListGenerationContext
{
    public required IReadOnlyList<WmsOrder> Orders { get; init; }

    public required DateTime Now { get; init; }

    public required PickListGenerationCounters Counters { get; init; }

    public int WorkerIndex { get; set; }

    /// <summary>
    /// When set, only these order items are processed (short-pick regeneration).
    /// </summary>
    public IReadOnlyList<WmsOrderItem>? ShortPickOrderItems { get; init; }

    /// <summary>
    /// When set, only these order items are processed (sort remaining items request).
    /// </summary>
    public IReadOnlyList<WmsOrderItem>? SortOrderItems { get; init; }

    /// <summary>
    /// When set, Step 2 uses this priority instead of order-based rules.
    /// </summary>
    public int? FixedPriority { get; init; }

    /// <summary>
    /// When true, Step 3 assigns a route-based drop location only.
    /// </summary>
    public bool UseRouteOnlyDropZone { get; init; }

    /// <summary>
    /// Maximum number of unique SKUs allowed in a single pick list (default: 30).
    /// </summary>
    public int MaxUniqueSkus { get; init; } = 30;

    /// <summary>
    /// In-memory order item allocations loaded from persisted InventoryAllocation rows (Step 1 output).
    /// </summary>
    public List<OrderItemAllocationDraft> OrderItemDrafts { get; } = [];

    /// <summary>
    /// Flattened, sorted allocation lines (Step 4 output).
    /// </summary>
    public List<PickListAllocationLineDraft> AllocationLines { get; } = [];

    /// <summary>
    /// Persisted pick lists (Step 5 output).
    /// </summary>
    public List<PickListEntity> PickLists { get; } = [];

    public long WarehouseId => Orders[0].WarehouseId;
}

internal sealed class PickListGenerationCounters
{
    public int OrdersProcessed;
    public int PickListsCreated;
    public int OrdersMarkedGenerated;
    public int OrdersDiscontinued;
    public int DropZoneStepsExecuted;
    public int WorkerAssignmentStepsExecuted;
    public int ItemsFullyAllocated;
    public int ItemsPartiallyAllocated;
    public int ItemsUnallocated;
    public int ItemsSkippedRouteFinalZone;
    public int WorkersAssigned;
    public int InventoryLedgerEntriesCreated;
    public int OutwardBatchesCreated;

    public GeneratePickListsResult ToResult() => new(
        OrdersProcessed,
        PickListsCreated,
        OrdersMarkedGenerated,
        OrdersDiscontinued,
        DropZoneStepsExecuted,
        WorkerAssignmentStepsExecuted,
        ItemsFullyAllocated,
        ItemsPartiallyAllocated,
        ItemsUnallocated,
        ItemsSkippedRouteFinalZone,
        WorkersAssigned);
}

internal static class PickListPriorityResolver
{
    //TODO: check exact customer type
    private const string HoreccaCustomerType = "horecca";

    public static int Resolve(WmsOrder order, int? fixedPriority = null)
    {
        if (fixedPriority.HasValue)
            return fixedPriority.Value;

        var orderType = order.OrderDeliveryType?.Trim().ToLowerInvariant();
        var isHorecca = order.CustomerType?.Equals(HoreccaCustomerType, StringComparison.OrdinalIgnoreCase) == true;
        var hasCluster = !string.IsNullOrWhiteSpace(order.ClusterId);

        return orderType switch
        {
            "quick" when isHorecca => 1,
            "quick" => 2,
            "standard" when hasCluster => 5,
            "standard" when isHorecca => 3,
            "standard" => 4,
            _ => 4
        };
    }
}
