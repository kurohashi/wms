namespace Wms.PickList.Application.Services.Cancellation;

public interface IPickListCancellationQueue
{
    ValueTask EnqueueAsync(Guid orderUuid, CancellationToken ct = default);
}
