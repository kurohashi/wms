using System;
using System.Threading;
using System.Threading.Tasks;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Wms.Contracts.Interfaces;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;
using Wms.PickList.Application.Services;

namespace Wms.PickList.Application.Command;

public class ReassignPickListWorkerCommand(Guid pickListUuid, ReassignPickListRequestDto dto) : ICommand<bool>
{
    public Guid PickListUuid { get; } = pickListUuid;
    public ReassignPickListRequestDto Dto { get; } = dto;

    public class Handler(IDispatcher dispatcher, WmsDbContextWrite dbContext, ICurrentUser currentUser) : ICommandHandler<ReassignPickListWorkerCommand, bool>
    {
        public async Task<bool> HandleAsync(ReassignPickListWorkerCommand command, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0;
            if (warehouseId == 0)
                throw new BusinessRuleException("User is not associated with any warehouse.");

            var request = command.Dto;

            if (request.WorkerUuid == null)
            {
                throw new BusinessRuleException("WorkerUuid is required for Worker reassignment.");
            }

            return await PickListWorkerReassignmentHelper.ReassignPickListWorkerAsync(
                dispatcher,
                dbContext,
                command.PickListUuid,
                request.WorkerUuid.Value,
                warehouseId,
                currentUser.UserId,
                ct);
        }
    }
}