namespace Wms.PickList.Application.Dto;
public sealed record ReportPickListDamageDto(
    long InventoryAllocationId,
    long Quantity,
    Guid BinUuid,
    long RejectionReasonId
);