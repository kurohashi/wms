using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Microsoft.EntityFrameworkCore;
using Wms.Domain.Entities;
using Wms.PickList.Application.Dto;
using Wms.Persistence.DbContexts;
using Dit.Shared.Exceptions;
using Wms.Domain.Enums;

namespace Wms.PickList.Application.Command;

public sealed class CreatePickListCommand(CreatePickListRequestDto dto, string createdBy) : ICommand<Guid>
{
    public CreatePickListRequestDto Dto { get; } = dto;
    public string CreatedBy { get; } = createdBy;

    public sealed class Handler(WmsDbContextWrite dbContext) : ICommandHandler<CreatePickListCommand, Guid>
    {
        public async Task<Guid> HandleAsync(CreatePickListCommand command, CancellationToken ct = default)
        {
            var dto = command.Dto;

            var warehouseExists = await dbContext.Warehouses
                .AnyAsync(w => w.Id == dto.WarehouseId && w.Status == true && w.IsDeleted == false, ct);

            if (!warehouseExists)
                throw new BusinessRuleException($"Warehouse with ID '{dto.WarehouseId}' not found.");

            if (dto.Items == null || dto.Items.Count == 0)
                throw new BusinessRuleException("At least one item is required to create a pick list.");

            var validStrategy = dto.PickingStrategy == PickStrategy.FIFO.ToString()
                             || dto.PickingStrategy == PickStrategy.FEFO.ToString();
            if (!validStrategy)
                throw new BusinessRuleException($"Invalid picking strategy '{dto.PickingStrategy}'. Must be FIFO or FEFO.");

            if (dto.OrderId.HasValue)
            {
                var orderExists = await dbContext.WmsOrders
                    .AnyAsync(o => o.Id == dto.OrderId.Value, ct);

                if (!orderExists)
                    throw new BusinessRuleException($"WmsOrder with ID '{dto.OrderId.Value}' not found.");
            }

            var pickList = new Wms.Domain.Entities.PickList
            {
                PickListUuid = Guid.NewGuid(),
                WarehouseId = dto.WarehouseId,
                Status = PickListStatus.PENDING.ToString(),
                PickingStrategy = dto.PickingStrategy,
                Priority = dto.Priority,
                IsActive = true,
                IsDeleted = false,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = command.CreatedBy
            };

            foreach (var item in dto.Items)
            {
                pickList.PickListItems.Add(new PickListItem
                {
                    PickListItemUuid = Guid.NewGuid(),
                    SkuId = item.SkuId,
                    RequestedQty = item.RequestedQty,
                    AllocatedQty = 0,
                    PickedQty = 0,
                    Uom = item.Uom,
                    Status = nameof(PickListItemStatus.PENDING),
                    IsActive = true,
                    IsDeleted = false,
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = command.CreatedBy
                });
            }

            await dbContext.PickLists.AddAsync(pickList, ct);
            await dbContext.SaveChangesAsync(ct);

            return pickList.PickListUuid;
        }
    }
}
