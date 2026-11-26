using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Domain.Entities;
using Wms.PickList.Application.Dto;
using Wms.Persistence.DbContexts;

namespace Wms.PickList.Application.Command;

public sealed class AddInventoryAllocationCommand(PickListItemAllocationDto dto) : ICommand<long>
{
    public PickListItemAllocationDto Dto { get; } = dto;

    public sealed class Handler(WmsDbContextWrite dbContext) : ICommandHandler<AddInventoryAllocationCommand, long>
    {
        public async Task<long> HandleAsync(AddInventoryAllocationCommand command, CancellationToken ct = default)
        {
            var dto = command.Dto;

            // create allocation
            var allocation = new InventoryAllocation
            {
                PickListItemId = dto.PickListItemId,
                LocationBatchId = dto.LocationInventoryBatchId,
                AllocatedQty = (int)dto.Quantity,
                
            };

            await dbContext.InventoryAllocations.AddAsync(allocation);

            await dbContext.SaveChangesAsync(ct);

            return allocation.Id;
        }
    }
}