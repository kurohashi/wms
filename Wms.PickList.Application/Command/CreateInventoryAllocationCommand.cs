using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Command;

public sealed class CreateInventoryAllocationCommand(CreateInventoryAllocationDto dto) : ICommand<long>
{
    public CreateInventoryAllocationDto Dto { get; } = dto;

    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser) : ICommandHandler<CreateInventoryAllocationCommand, long>
    {
        public async Task<long> HandleAsync(CreateInventoryAllocationCommand command, CancellationToken ct = default)
        {
            try
            {
                var dto = command.Dto;

                long warehouseId = currentUser.WarehouseId ?? 0; if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

                var _pickListItem = await dbContext.PickListItems.FirstOrDefaultAsync(p => p.PickListItemUuid == dto.PickListItemUuid, ct) ??
                    throw new BusinessRuleException($"PickListItem with UUID {dto.PickListItemUuid} not found.");

                var bin = await dbContext.Bins.FirstOrDefaultAsync(b => b.BinUuid == dto.BinUuid, ct) ??
                    throw new BusinessRuleException($"Bin with UUID {dto.BinUuid} not found.");

                var batch = await dbContext.Batches.FirstOrDefaultAsync(b => b.BatchUuid == dto.BatchUuid, ct) ??
                    throw new BusinessRuleException($"Batch with UUID {dto.BatchUuid} not found.");

                var locationInventory = await dbContext.LocationInventoryBatches.FirstOrDefaultAsync(x => x.WarehouseId == warehouseId
                && x.BatchId == batch.Id && x.LocationId == bin.Id && x.SkuId == batch.SkuId, ct) ??
                throw new BusinessRuleException($"No bacth is mapped with skuId {batch.SkuId} in locationInventory.");

                var pickListItemListWithSameSKU = await dbContext.PickListItems.Where(p => p.SkuId == _pickListItem.SkuId &&
                p.PickListId == _pickListItem.PickListId && p.IsActive == true && p.IsDeleted == false).ToListAsync(ct);

                foreach (var pickListItem in pickListItemListWithSameSKU)
                {
                    var allocatedQty = (int)pickListItem.AllocatedQty.GetValueOrDefault();
                    var pickedQty = (int)(pickListItem.RequestedQty - pickListItem.PickedQty.GetValueOrDefault());

                    if (allocatedQty <= 0 || pickedQty <= 0)
                    {
                        throw new BusinessRuleException($"PickListItem with UUID {dto.PickListItemUuid} has no available quantity to allocate.");
                    }

                    var allocation = new InventoryAllocation
                    {
                        PickListItemId = pickListItem.Id,
                        InventoryStockId = 0,
                        AllocatedQty = allocatedQty,
                        StrategyUsed = "FEFO",
                        Sequence = 1,
                        IsActive = true,
                        IsDeleted = false,
                        CreatedBy = currentUser.UserId,
                        CreatedDate = DateTime.UtcNow,
                        LocationBatchId = locationInventory.Id,
                        OrderItemId = pickListItem.OrderItemId.GetValueOrDefault(),
                        FromBin = bin.Id,
                        Status = null,
                        IsLeftover = false,
                        BatchId = batch.Id,
                        PickedQty = null,
                        IsPickable = true,
                    };

                    await dbContext.InventoryAllocations.AddAsync(allocation, ct);

                    //add in new table BinInventoryReservation
                    var binReservations = new BinInventoryReservation()
                    {
                        InventoryAllocation = allocation,
                        BinId = bin.Id,
                        ReservedQty = allocatedQty,
                        PickedQty = allocatedQty,
                        Skuid = batch.SkuId,
                        OrderItemId = pickListItem.OrderItemId.GetValueOrDefault(),
                        Status = nameof(InventoryStatus.RESERVED),
                        CreatedAt = DateTime.UtcNow,
                        IsSold = false,
                    };
                    await dbContext.BinInventoryReservations.AddAsync(binReservations, ct);

                    await dbContext.SaveChangesAsync(ct);
                }
                return 1;
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
}
