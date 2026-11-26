using Wms.Domain.Entities;

namespace Wms.PickList.Application.Services.Generation;

internal sealed class OrderItemAllocationDraft
{
    public required WmsOrder Order { get; init; }

    public required WmsOrderItem OrderItem { get; init; }

    public long SkuId { get; init; }

    public decimal RequestedQty { get; set; }

    public bool IsBulkPick { get; set; }

    public int Priority { get; set; }

    public long? DropLocationId { get; set; }

    public string? Status { get; set; }

    public List<BinAllocationDraft> BinAllocations { get; } = [];
}

internal sealed class BinAllocationDraft
{
    public long InventoryAllocationId { get; init; }

    public long LocationBatchId { get; init; }

    public long BinId { get; init; }

    public long BatchId { get; init; }

    public long InventoryStockId { get; init; }

    public decimal AllocatedQty { get; set; }

    public DateOnly? BatchExpiry { get; init; }

    public bool IsPickable { get; init; }

    public bool IsRoutable { get; init; }

    public bool IsFromClusterBin { get; init; }

    public string? BinStorageType { get; init; }

    public Guid? StorageTypeId { get; init; }

    public long? SkillId { get; init; }

    public decimal SkillCapacity { get; init; }

    public decimal? BinSequence { get; init; }
}

internal sealed class PickListAllocationLineDraft
{
    public required WmsOrder Order { get; init; }

    public required WmsOrderItem OrderItem { get; init; }

    public long SkuId { get; init; }

    public decimal RequestedQty { get; init; }

    public decimal AllocatedQty { get; init; }

    public int Priority { get; init; }

    public long? DropLocationId { get; init; }

    public Guid? StorageTypeId { get; init; }

    public bool IsFromClusterBin { get; init; }

    public long InventoryAllocationId { get; init; }

    public long LocationBatchId { get; init; }

    public long BinId { get; init; }

    public long BatchId { get; init; }

    public long InventoryStockId { get; init; }

    public long? SkillId { get; init; }

    public decimal SkillCapacity { get; init; }

    public decimal? BinSequence { get; init; }

    public string? ItemStatus { get; init; }

    // #PicklistWeight_New: Unit weight calculation defaulting to 0 if product weight is missing or non-positive
    public decimal UnitWeight => OrderItem.Weight is > 0 ? OrderItem.Weight.Value : 0m;

    // #PicklistWeight_New: Total accumulated weight for this line
    public decimal TotalWeight => AllocatedQty * UnitWeight;
}

internal sealed class PickListDraft
{
    public long? DropLocationId { get; init; }

    public Guid? StorageTypeId { get; init; }

    public int Priority { get; init; }

    public long? SkillId { get; init; }

    public bool IsFromClusterBin { get; init; }

    public List<PickListAllocationLineDraft> Lines { get; } = [];
}
