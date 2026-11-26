namespace Wms.PickList.Application.Dto;

public sealed record UpdatePickListPriorityDto
(
    Guid PickListUuid,
    string Priority
);