using System.Collections.Generic;

namespace Wms.PickList.Application.Dto;

public sealed record BinPickDto(
    Guid BinUuid,
    int PickQty,
    long? LocationInventoryBatchId
);

public sealed record NextPickingRequestDto(
    Guid PickListItemUuid,
    Guid WorkerUuid,
    List<BinPickDto> Bins
);

public sealed record NextPickingResponseDto(
    bool Success,
    string Message
);