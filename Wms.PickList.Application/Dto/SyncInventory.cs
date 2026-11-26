namespace Wms.PickList.Application.Dto;

public sealed record SkuQuantityDto(
    long Sku,
    int Quantity,
    long WarehouseId
);
