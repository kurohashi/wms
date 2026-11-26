namespace Wms.PickList.Application.Dto;

public sealed record CreatePickListRequestDto(
    long WarehouseId,
    long? OrderId,
    string PickingStrategy,    // FIFO | FEFO
    int? Priority,
    List<CreatePickListItemDto> Items
);

public sealed record CreatePickListItemDto(
    long SkuId,
    long? BatchId,
    decimal RequestedQty,
    string? Uom
);
