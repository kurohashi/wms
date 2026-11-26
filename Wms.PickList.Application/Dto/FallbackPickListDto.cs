using System;
using System.Collections.Generic;

namespace Wms.PickList.Application.Dto;

public sealed record FallbackPickListRequestDto(
    string PickListNumber
);

public sealed record FallbackBinPickDetailDto(
    long BinId,
    Guid BinUuid,
    string BinCode,
    int PickedQty,
    int StockOnHand
);

public sealed record FallbackPickItemSummaryDto(
    Guid PickListItemUuid,
    long SkuId,
    int AllocatedQty,
    int PickedQty,
    int ShortQty,
    string Status,
    List<FallbackBinPickDetailDto> BinPicks
);

public sealed record FallbackPickListResultDto(
    bool Success,
    string Message,
    string PickListNumber,
    Guid? PickListUuid,
    string? PickListStatus,
    int TotalItemsProcessed,
    int TotalPickedQty,
    int TotalShortQty,
    List<FallbackPickItemSummaryDto> PickedItems
);

public sealed record FallbackPickListResponseDto(
    bool Success,
    string Message,
    int TotalPickListsRequested,
    int TotalPickListsSucceeded,
    int TotalPickListsFailed,
    List<FallbackPickListResultDto> Results
);
