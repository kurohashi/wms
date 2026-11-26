namespace Wms.PickList.Application.Services;

public interface IGeneratePickListsQueue
{
    ValueTask EnqueueAsync(IReadOnlyList<long> orderIds, CancellationToken ct = default);
}
