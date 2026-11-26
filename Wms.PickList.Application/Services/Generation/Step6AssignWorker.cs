using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.Contracts.HttpCurrentUser;
using Wms.Contracts.Interfaces;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.Work.Application.Worker.Query;

namespace Wms.PickList.Application.Services.Generation;

/// <summary>
/// Step 6: Create PICK tasks for allocated pick lists and assign them to warehouse workers (round-robin).
/// </summary>
internal sealed class Step6AssignWorker(
    WmsDbContextWrite dbContext,
    ICurrentUser currentUser,
    IFcmNotificationService? fcmService = null,
    ILogger<Work.Application.Tasks.Comand.AddTaskCommand.Handler>? logger = null) : PickListGenerationStepBase(dbContext)
{
    private const string PickTaskType = nameof(TaskTypeEnum.PICK);
    private const string PickListReferenceName = "PickList";

    protected override async Task ExecuteCoreAsync(PickListGenerationContext context, CancellationToken ct)
    {
        var workers = await LoadAssignableWorkersAsync(context, ct);
        //if (workers.Count == 0)
        //    return;

        foreach (var pickList in context.PickLists)
            await AssignWorkerAsync(context, pickList, workers, ct);
    }

    /// <summary>
    /// Loads the workers eligible for PICK tasks in the context's warehouse. Call once before assigning.
    /// </summary>
    public async Task<IReadOnlyList<Wms.Domain.Entities.Worker>> LoadAssignableWorkersAsync(
        PickListGenerationContext context,
        CancellationToken ct)
    {
        if (context.PickLists.Count == 0)
            return [];

        var warehouseId = context.WarehouseId;

        if (currentUser.WarehouseId == null || currentUser.WarehouseId == 0)
            currentUser = new SystemUser(warehouseId);

        GetAssignableWorkersQuery.Handler getAssignableWorkersHandler = new(DbContext, currentUser);
        var assignableResult = await getAssignableWorkersHandler.HandleAsync(new GetAssignableWorkersQuery
        {
            TaskType = PickTaskType,
            Page = 1,
            PageSize = 1000
        }, ct);

        var assignableWorkerUuids = assignableResult.Results.Select(r => r.WorkerUuid).ToList();
        if (assignableWorkerUuids.Count == 0)
            return [];

        return await DbContext.Workers.Where(w => assignableWorkerUuids.Contains(w.WorkerUuid)).OrderBy(w => w.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Creates a PICK task for a single pick list and assigns it to the next worker (round-robin).
    /// </summary>
    public async Task AssignWorkerAsync(
        PickListGenerationContext context,
        Wms.Domain.Entities.PickList pickList,
        IReadOnlyList<Wms.Domain.Entities.Worker> workers,
        CancellationToken ct)
    {
        if (pickList.Status != nameof(PickListStatus.PENDING))
            return;

        var worker = null as Wms.Domain.Entities.Worker;
        if (workers.Count > 0)
        {
            worker = workers[context.WorkerIndex % workers.Count];
            context.WorkerIndex++;
        }

        var dto = new Work.Application.Tasks.Dto.AddTaskDto
        {
            TaskName = $"Pick-{pickList.PickListNumber}",
            Type = PickTaskType,
            Priority = pickList.Priority ?? 1,
            ReferenceId = pickList.PickListUuid,
            ReferenceName = PickListReferenceName,
            OrderRef = pickList.PickListNumber,
            WorkerUuidList = workers.Count > 0 ? [worker!.WorkerUuid] : []
        };

        var addTaskHandler = new Work.Application.Tasks.Comand.AddTaskCommand.Handler(DbContext, currentUser, fcmService, logger);
        await addTaskHandler.HandleAsync(new Work.Application.Tasks.Comand.AddTaskCommand(dto), ct);

        pickList.AssignedWorkerId = worker.Id;
        pickList.ModifiedDate = context.Now;

        context.Counters.WorkersAssigned++;
        context.Counters.WorkerAssignmentStepsExecuted++;
    }
}
