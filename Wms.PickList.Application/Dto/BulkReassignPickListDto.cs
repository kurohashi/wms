using System;
using System.Collections.Generic;

namespace Wms.PickList.Application.Dto;

public sealed class BulkReassignPickListRequestDto
{
    public List<Guid> PickListUuids { get; set; } = new();
    public Guid WorkerUuid { get; set; }
}

public sealed class BulkReassignPickListResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public List<BulkReassignItemResultDto> Results { get; set; } = new();
}

public sealed class BulkReassignItemResultDto
{
    public Guid PickListUuid { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}