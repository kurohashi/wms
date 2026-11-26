
namespace Wms.PickList.Application.Dto
{
    public sealed record PickListItemAllocationDto(
        long PickListItemId,
        long LocationInventoryBatchId,
        decimal Quantity
    );
}