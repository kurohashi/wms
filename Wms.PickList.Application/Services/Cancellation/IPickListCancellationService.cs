namespace Wms.PickList.Application.Services.Cancellation;

public interface IPickListCancellationService
{
    Task CancelPickListsAsync(Guid orderUuid, CancellationToken ct);
}
