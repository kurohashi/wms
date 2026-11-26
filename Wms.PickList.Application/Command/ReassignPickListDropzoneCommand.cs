using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;
using Dit.Shared.Exceptions;
using Wms.Domain.Enums;
using Wms.Contracts.Interfaces;

namespace Wms.PickList.Application.Command;

public class ReassignPickListDropzoneCommand(Guid pickListUuid, ReassignPickListRequestDto dto) : ICommand<bool>
{
    public Guid PickListUuid { get; } = pickListUuid;
    public ReassignPickListRequestDto Dto { get; } = dto;

    public class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser) : ICommandHandler<ReassignPickListDropzoneCommand, bool>
    {
        public async Task<bool> HandleAsync(ReassignPickListDropzoneCommand command, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0; if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

            var dto = command.Dto;
            ValidateDropzoneDto(dto);

            var pickList = await GetAndValidatePickListAsync(dbContext, warehouseId, command.PickListUuid, ct);

            ValidateDropzoneNotDuplicate(pickList, dto.DropLocationId!.Value);
            await ValidateDropzoneBinAsync(dbContext, dto.DropLocationId.Value, ct);

            pickList.DropLocationId = dto.DropLocationId.Value;
            pickList.ModifiedBy = currentUser.UserId;
            pickList.ModifiedDate = DateTime.UtcNow;
            dbContext.Update(pickList);
            var result = await dbContext.SaveChangesAsync(ct);
            return result > 0 ? true : false;
        }

        private static async Task ValidateDropzoneBinAsync(WmsDbContextWrite dbContext, long dropBinId, CancellationToken ct)
        {
            var bin = await dbContext.Bins
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == dropBinId && b.IsActive == true && b.IsDeleted == false, ct)
                ?? throw new BusinessRuleException($"Drop bin with ID {dropBinId} not found.");

            if (!bin.IsRoutable && !bin.IsClusterBased)
            {
                throw new BusinessRuleException($"Bin '{bin.Code ?? dropBinId.ToString()}' is not a valid drop zone. Drop bins must have IsRoutable or IsClusterBased set to true.");
            }
        }
        private static async Task<Domain.Entities.PickList> GetAndValidatePickListAsync(WmsDbContextWrite dbContext, long warehouseId, Guid pickListUuid, CancellationToken ct)
        {
            var pickList = await dbContext.PickLists.FirstOrDefaultAsync(x => x.PickListUuid == pickListUuid &&
            x.IsDeleted != true && x.IsActive == true && x.WarehouseId == warehouseId, ct)
                ?? throw new BusinessRuleException($"PickList with UUID {pickListUuid} not found.");

            if (string.Equals(pickList.Status, nameof(PickListStatus.COMPLETED), StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessRuleException("Cannot reassign dropzone for a completed PickList.");
            }
            return pickList;
        }

        private static void ValidateDropzoneNotDuplicate(Domain.Entities.PickList pickList, long newDropzoneId)
        {
            if (pickList.DropLocationId == newDropzoneId)
            {
                throw new BusinessRuleException($"DropLocation {newDropzoneId} is already assigned to this PickList.");
            }
        }

        private static void ValidateDropzoneDto(ReassignPickListRequestDto dto)
        {
            if (dto.DropLocationId == null)
            {
                throw new BusinessRuleException("DropLocationId is required.");
            }
        }
    }
}