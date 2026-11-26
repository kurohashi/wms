namespace Wms.PickList.Application.Dto;

public sealed record ReportedDamageDetailDto(
    long Id,
    long LocationId,
    string? BinCode,
    long BatchId,
    string? BatchNumber,
    decimal Quantity,
    long SkuId,
    string? ProductName,
    long RejectionReasonId,
    string? RejectionReasonName,
    string? Type,
    string? Remarks,
    DateTime CreatedDate
);
