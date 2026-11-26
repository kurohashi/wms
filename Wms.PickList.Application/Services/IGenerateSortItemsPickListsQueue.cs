namespace Wms.PickList.Application.Services;

public sealed record SortItemsPickListOrderRequest(long OrderId, IReadOnlyList<long> OrderItemIds);

public interface IGenerateSortItemsPickListsQueue
{
    ValueTask EnqueueAsync(
        Guid routeUuid,
        IReadOnlyList<SortItemsPickListOrderRequest> orders,
        CancellationToken ct = default);
}
