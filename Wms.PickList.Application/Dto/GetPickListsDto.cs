using System;
using Wms.Domain.Enums;
using Wms.PickList.Application.Enums;

namespace Wms.PickList.Application.Dto;

public sealed record PickListSummaryDto(
    Guid PickListUuid,
    string? PickListNumber,
    int TotalItems,
    string Status,
    string? DropLocation,
    string? ZoneCode,
    DateTime? CreatedAt,
    PickerDto? Picker,
    int? Priority,
    decimal? percentComplete,
    int? TimeSinceLastActivityInMin,
    List<string> Orders,
    List<string> FromBins,
    string? OrderDeliveryType = null,
    string? CustomerType = null
);

public sealed record PickerDto(
    string Id,
    string Name
);



public class GetPickListsRequest
{
    public PickListStatus? Status { get; init; }
    public int? WarehouseId = null;
    public int Page { get; init; } = 1;
    public int Limit { get; init; } = 20;
}

public sealed record PickListStatsCountsDto(
    int TotalPickListCount,
    int Pending,
    int Assigned,
    int InProgress,
    int Completed,
    int Cancelled
);