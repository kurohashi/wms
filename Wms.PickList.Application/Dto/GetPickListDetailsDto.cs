using System.Runtime.InteropServices;

namespace Wms.PickList.Application.Dto;

public sealed record PickListDetailsDto
(
    Guid PickListUuid,
    string PickListNumber,
    int TotalAmount,
    int TotalItems,
    string Status,
    DateTime? CreatedAt,
    string DropZoneLocation,
    string DropLocation,
    bool DropBinIsScanMandatory,
    string DropLocationUuid,
    string? ZoneCode,
    string? assigneeName,
    int? Priority,
    List<PickListOrderDto> Orders,
    List<PickListProductDto> Products
);

public record PickListOrderDto(
    Guid OrderUuid,
    string UniqueOrderId
);

public record PickListProductDto(
    Guid PickListItemUuid,
    long Sku,
    string ProductName,
    int Quantity,
    string Uom,
    int PickedQty,
    int ShortQty,
    long InventoryAllocationId,
    bool SuggestedBins,
    long DamageQty,
    List<BinBatchDto> BinDetails,
    string? ProductImage,
    bool IsScanMandatory = false,
    string? ProductEan = null
)
{
    public string? ProductEanNumber => ProductEan;
}

public record BinBatchDto(
    Guid BinUuid,
    string BinCode,
    DateOnly? ExpiryDate,
    long LocationInventoryBatchId,
    int PickedQty,
    int AvailableQty,
    bool RouteBased,
    bool ClusterBased,
    bool IsLocked,
    decimal? Sequence,
    long StockOnHand,
    long DamageQty,
    long? Position,
    string? Zone,
    string? Aisle,
    long? Rack,
    long? Bay,
    long? Level,
    bool IsScanMandatory
);