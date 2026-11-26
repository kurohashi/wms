namespace Wms.PickList.Application.Dto;

public sealed record GeneratePickListsQueuedResponse(
    int BatchSize,
    string Status = "IN_PROGRESS",
    string Message = "Pick list generation request is in progress.");
