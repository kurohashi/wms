namespace Wms.PickList.Application.Dto;

public sealed record WorkerPickListDto(
    String PickListNumber,
    Guid PickListUuid,
    Guid AssignmentUuid,
    Guid TaskUuid,
    int? Priority,
    string Status,
    int SkuCompleteCount,
    decimal SkuCount,
    string DropPoint
);

public sealed record WorkerPickListTaskDto(
    Guid AssignmentUuid,
    Guid TaskUuid,
    string TaskName,
    DateTime AssignedAt
);