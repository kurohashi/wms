using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Wms.PickList.Application.Services;

namespace Wms.PickList.Application.Command;

public sealed class ReleaseOrderInventoryCommand(long orderId) : ICommand<ReleaseOrderInventoryResult>
{
    public long OrderId { get; } = orderId;

    public sealed class Handler(OrderInventoryAllocationService allocationService)
        : ICommandHandler<ReleaseOrderInventoryCommand, ReleaseOrderInventoryResult>
    {
        public async Task<ReleaseOrderInventoryResult> HandleAsync(
            ReleaseOrderInventoryCommand command,
            CancellationToken ct = default) =>
            await allocationService.ReleaseInventoryAsync(command.OrderId, ct);
    }
}
