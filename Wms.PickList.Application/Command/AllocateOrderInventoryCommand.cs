using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Wms.PickList.Application.Services;

namespace Wms.PickList.Application.Command;

public sealed class AllocateOrderInventoryCommand(long orderId) : ICommand<AllocateOrderInventoryResult>
{
    public long OrderId { get; } = orderId;

    public sealed class Handler(OrderInventoryAllocationService allocationService)
        : ICommandHandler<AllocateOrderInventoryCommand, AllocateOrderInventoryResult>
    {
        public async Task<AllocateOrderInventoryResult> HandleAsync(
            AllocateOrderInventoryCommand command,
            CancellationToken ct = default) =>
            await allocationService.AllocateAsync(command.OrderId, ct);
    }
}
