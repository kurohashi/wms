using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Command;

public sealed record UpdatePickListPriorityResponseDto(
    Guid PickListUuid,
    string Message
);

public sealed class UpdatePickListPriorityCommand(UpdatePickListPriorityDto dto)
    : ICommand<UpdatePickListPriorityResponseDto>
{
    public UpdatePickListPriorityDto Dto { get; } = dto;

    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser)
        : ICommandHandler<UpdatePickListPriorityCommand, UpdatePickListPriorityResponseDto>
    {
        public async Task<UpdatePickListPriorityResponseDto> HandleAsync(
            UpdatePickListPriorityCommand command,
            CancellationToken ct = default)
        {
            var warehouseId = currentUser.WarehouseId;
            if (warehouseId == 0) throw new BusinessRuleException("User not associated with any warehouse.");

            var dto = command.Dto;

            var picklist = await dbContext.PickLists
                .Where(p => p.PickListUuid == dto.PickListUuid && p.WarehouseId == warehouseId)
                .FirstOrDefaultAsync(ct);
            if (picklist == null) throw new BusinessRuleException($"No Picklist associated with {dto.PickListUuid}");


            var task = await dbContext.Tasks
                .Where(x => x.ReferenceId == picklist.PickListUuid)
                .FirstOrDefaultAsync(ct);

            if (task == null)
            {
                throw new BusinessRuleException($"Task with PickListUuid {dto.PickListUuid} not found.");
            }

            switch (dto.Priority)
            {
                case "P0":
                    task.Priority = 0;
                    picklist.Priority = task.Priority;
                    break;
                case "P1":
                    task.Priority = 1;
                    picklist.Priority = task.Priority;
                    break;
                case "P2":
                    task.Priority = 2;
                    picklist.Priority = task.Priority;
                    break;
                case "P3":
                    task.Priority = 3;
                    picklist.Priority = task.Priority;
                    break;
                case "P4":
                    task.Priority = 4;
                    picklist.Priority = task.Priority;
                    break;
                case "P5":
                    task.Priority = 5;
                    picklist.Priority = task.Priority;
                    break;
                default:
                    throw new BusinessRuleException($"Invalid priority value: {dto.Priority}. Valid values are P0, P1, P2, P3, P4, P5");
            }
            await dbContext.SaveChangesAsync(ct);

            return new UpdatePickListPriorityResponseDto(
                PickListUuid: dto.PickListUuid,
                Message: $"Picklist priority updated to {dto.Priority} successfully."
            );
        }
    }
}