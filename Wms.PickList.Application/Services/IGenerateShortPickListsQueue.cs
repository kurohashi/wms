namespace Wms.PickList.Application.Services;

public interface IGenerateShortPickListsQueue
{
    ValueTask EnqueueAsync(
        Guid routeUuid,
        long orderId,
        IReadOnlyList<long> orderItemIds,
        CancellationToken ct = default);
}
