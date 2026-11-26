using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Inventory.Application.ProductInventory.Command;
using Wms.Inventory.Application.ProductInventory.Dto;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Command;

public sealed record ReportPickListDamageResponseDto(
    long Id,
    string Message
);

public sealed class ReportPickListDamageCommand(ReportPickListDamageDto dto) : ICommand<ReportPickListDamageResponseDto>
{
    public ReportPickListDamageDto Dto { get; } = dto;

    public sealed class Handler(WmsDbContextWrite dbContext, IDispatcher dispatcher, ICurrentUser currentUser) : ICommandHandler<ReportPickListDamageCommand, ReportPickListDamageResponseDto>
    {
        public async Task<ReportPickListDamageResponseDto> HandleAsync(ReportPickListDamageCommand command, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0;
            if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

            var dto = command.Dto;

            if (dto.Quantity <= 0)
                throw new BusinessRuleException("Quantity must be greater than 0.");

            var bin = await dbContext.Bins.FirstOrDefaultAsync(b => b.BinUuid == dto.BinUuid, ct)
                ?? throw new BusinessRuleException($"Bin with UUID {dto.BinUuid} not found.");

            // Fetch LocationId from InventoryAllocation table
            var picklistItemId = await dbContext.InventoryAllocations.Where(ia => ia.Id == dto.InventoryAllocationId)
                .Select(ia => ia.PickListItemId).FirstOrDefaultAsync(ct)
                ?? throw new BusinessRuleException($"PickListItem with Id {dto.InventoryAllocationId} not found.");

            var inventoryAllocation = await dbContext.InventoryAllocations.Include(ia => ia.FromBinNavigation)
                .Where(ia => ia.PickListItemId == picklistItemId && ia.FromBin == bin.Id).FirstOrDefaultAsync(ct)
                ?? throw new BusinessRuleException($"InventoryAllocation with Id {dto.InventoryAllocationId} and Bin Code {bin.Code} not found.");

            var picklistItem = await dbContext.PickListItems
             .Where(p => p.Id == inventoryAllocation!.PickListItemId)
             .FirstOrDefaultAsync(ct)
             ?? throw new BusinessRuleException($"PickListItem with Id {inventoryAllocation!.PickListItemId} not found.");

            // Fetch SkuId from PickListItems table
            var pickListItemSkuId = picklistItem.SkuId;

            // Check if reported Bin is picking bin or storage bin
            // If picking bin, then reduce the available quantity in LocationInventoryBatches table
            if (inventoryAllocation.LocationBatchId != null && inventoryAllocation.IsPickable == true && inventoryAllocation.FromBinNavigation != null
                && inventoryAllocation.FromBinNavigation.IsClusterBased == false && inventoryAllocation.FromBinNavigation.IsRoutable == false
            )
            {
                var locationInventoryBatch = await dbContext.LocationInventoryBatches.FirstOrDefaultAsync(x => x.Id == inventoryAllocation.LocationBatchId, ct);
                locationInventoryBatch?.AvailableQty -= dto.Quantity;

                //add in inventory ledger
                await dbContext.InventoryLedgers.AddAsync(
                new InventoryLedger
                {
                    LedgerUuid = Guid.NewGuid(),
                    WarehouseId = warehouseId,
                    SkuId = locationInventoryBatch.SkuId,
                    BatchId = locationInventoryBatch.BatchId,
                    BinId = locationInventoryBatch.LocationId,
                    ReferenceType = "PICKLIST-REPORT-DAMAGE",
                    ReferenceId = picklistItem.Id,
                    ReferenceNumber = null,
                    Quantity = dto.Quantity,
                    Direction = nameof(InventoryDirection.OUT),
                    TransactionTime = DateTime.UtcNow,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedDate = DateTime.UtcNow
                }, ct);
            }

            // If Reporting Damage from Cluster/Routing Bin 
            //Fetch PickListItemId from InventoryAllocation table and create a PutawayRejection entry
            long? pickListItemId = null;
            if (inventoryAllocation.LocationBatchId == null && inventoryAllocation.PickListItemId != null
                && inventoryAllocation.FromBinNavigation != null && inventoryAllocation.IsPickable == false)
            {
                pickListItemId = inventoryAllocation.PickListItemId.Value;
            }

            // Validate Bin Code
            //if(inventoryAllocation.FromBinNavigation.Code != dto.BinCode)
            //    throw new BusinessRuleException($"Bin Code {dto.BinCode} is not same as the FromBin in InventoryAllocation.");
            
            var rejection = new PutawayRejection
            {
                LocationId = inventoryAllocation!.FromBin ?? 0,
                BatchId = inventoryAllocation.BatchId ?? 0,
                Quantity = dto.Quantity,
                SkuId = pickListItemSkuId,
                RejectionReasonId = dto.RejectionReasonId,
                PickListItemId = pickListItemId,
                Type = "PICKLIST-REPORT-DAMAGE",
                CreatedDate = DateTime.UtcNow
            };

            await dbContext.PutawayRejections.AddAsync(rejection, ct);
            await dbContext.SaveChangesAsync(ct);

            await dispatcher.SendAsync<TransactInventoryCommand, TransactInventoryResponseDto>(new TransactInventoryCommand(new TransactInventoryRequestDto
            (
                WarehouseId : warehouseId,
                SkuId : pickListItemSkuId,
                Quantity : -(dto.Quantity),
                StockType : "DAMAGED",
                IsSyncToZoho: true
            )), ct);

            return new ReportPickListDamageResponseDto(rejection.Id, "Damage reported successfully.");
        }
    }
}
