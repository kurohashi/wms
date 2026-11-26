using System;
using System.Threading;
using System.Threading.Tasks;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.Work.Application.Tasks.Comand;
using Wms.Work.Application.Tasks.Dto;

namespace Wms.PickList.Application.Services;

public static class PickListWorkerReassignmentHelper
{
    public static async Task<bool> ReassignPickListWorkerAsync(
        IDispatcher dispatcher,
        WmsDbContextWrite dbContext,
        Guid pickListUuid,
        Worker worker,
        long warehouseId,
        string? userId,
        CancellationToken ct = default)
    {
        var existingTask = await dbContext.Tasks.Include(t => t.Assignments)
            .FirstOrDefaultAsync(t => t.ReferenceId == pickListUuid && t.WarehouseId == warehouseId &&
            t.Type == nameof(TaskTypeEnum.PICK) && t.IsActive && !t.IsDeleted, ct) ??
                throw new BusinessRuleException($"No active PICK task found for PickList {pickListUuid}.");

        var activeAssignment = existingTask.Assignments.FirstOrDefault(a => a.IsActive && !a.IsDeleted &&
            a.Status != nameof(AssignmentStatusEnum.COMPLETED)) ??
            throw new BusinessRuleException($"An active PICK task exists for PickList {pickListUuid}, but no valid active assignment was found to reassign.");

        var reassignWorkerDto = new ReassignWorkerDto
        {
            AssignmentUuid = activeAssignment.AssignmentUuid,
            WorkerUuid = worker.WorkerUuid
        };

        var result = await dispatcher.SendAsync<ReassignWorkerCommand, bool>(new ReassignWorkerCommand(reassignWorkerDto), ct);

        var pickList = await dbContext.PickLists.FirstOrDefaultAsync(x => x.IsDeleted != true && x.IsActive == true
            && x.PickListUuid == pickListUuid && x.WarehouseId == warehouseId, ct);

        if (pickList != null)
        {
            pickList.AssignedWorkerId = worker.Id;
            pickList.ModifiedBy = userId;
            pickList.ModifiedDate = DateTime.UtcNow;
            dbContext.Update(pickList);
            await dbContext.SaveChangesAsync(ct);
        }

        return result;
    }

    public static async Task<bool> ReassignPickListWorkerAsync(
        IDispatcher dispatcher,
        WmsDbContextWrite dbContext,
        Guid pickListUuid,
        Guid workerUuid,
        long warehouseId,
        string? userId,
        CancellationToken ct = default)
    {
        var worker = await dbContext.Workers.FirstOrDefaultAsync(x => !x.IsDeleted && x.IsActive &&
            x.WorkerUuid == workerUuid && x.WarehouseId == warehouseId, ct)
            ?? throw new BusinessRuleException($"Worker with UUID {workerUuid} not found.");

        return await ReassignPickListWorkerAsync(dispatcher, dbContext, pickListUuid, worker, warehouseId, userId, ct);
    }
}