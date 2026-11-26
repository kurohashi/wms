using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wms.Domain.Entities;
using Wms.PickList.Application.Command;
using Wms.PickList.Application.Common;
using Wms.PickList.Application.Services.Generation;
using Wms.Persistence.DbContexts;
using Wms.Contracts.Interfaces;
using Task = System.Threading.Tasks.Task;
namespace Wms.PickList.Application.Services;

/// <summary>
/// Orchestrates pick list generation through a 6-step pipeline for a batch of orders:
/// load existing allocations → validate from bins → set priority → select drop zone → sort → break into pick lists → assign worker.
/// </summary>
public sealed class PickListGenerationService(
    WmsDbContextWrite dbContext,
    ICurrentUser currentUser,
    OrderInventoryAllocationService allocationService,
    IOptions<PickListSettings>? settings = null,
    IFcmNotificationService? fcmService = null,
    ILogger<Work.Application.Tasks.Comand.AddTaskCommand.Handler>? taskLogger = null)
{
    private readonly PickListGenerationStepBase _pipeline = BuildPipeline(dbContext, allocationService);
    private readonly Step6AssignWorker _assignWorkerStep = new(dbContext, currentUser, fcmService, taskLogger);
    private readonly int _maxUniqueSkus = settings?.Value?.MaxUniqueSkus is > 0 ? settings.Value.MaxUniqueSkus : 30;

    public Task<PickListGenerationRunResult> GenerateAsync(
        IReadOnlyList<WmsOrder> orders,
        CancellationToken ct = default) =>
        GenerateAsync(orders, new PickListGenerationOptions(), ct);

    public async Task<PickListGenerationRunResult> GenerateAsync(
        IReadOnlyList<WmsOrder> orders,
        PickListGenerationOptions options,
        CancellationToken ct = default)
    {
        var counters = new PickListGenerationCounters();
        if (orders.Count == 0)
            return new PickListGenerationRunResult(counters.ToResult(), options.WorkerIndex);

        var now = DateTime.UtcNow;

        var context = new PickListGenerationContext
        {
            Orders = orders,
            Now = now,
            Counters = counters,
            WorkerIndex = options.WorkerIndex,
            FixedPriority = options.FixedPriority,
            UseRouteOnlyDropZone = options.UseRouteOnlyDropZone,
            MaxUniqueSkus = _maxUniqueSkus
        };

        await _pipeline.ExecuteAsync(context, ct);
        counters.OrdersProcessed = orders.Count;

        await dbContext.SaveChangesAsync(ct);

        await AssignWorkersAsync(context, ct);

        return new PickListGenerationRunResult(counters.ToResult(), context.WorkerIndex);
    }

    public async Task<GenerateShortPickListsResult> GenerateShortPickAsync(
        WmsOrder order,
        IReadOnlyList<WmsOrderItem> shortPickItems,
        CancellationToken ct = default)
    {
        var counters = new PickListGenerationCounters();
        var now = DateTime.UtcNow;


        foreach (var item in shortPickItems)
        {
            item.ConfirmedQuantity = null;
            item.Status = "PENDING";
            item.UpdatedAt = now;
        }

        var context = new PickListGenerationContext
        {
            Orders = [order],
            Now = now,
            Counters = counters,
            WorkerIndex = 0,
            ShortPickOrderItems = shortPickItems,
            FixedPriority = 0,
            UseRouteOnlyDropZone = true,
            MaxUniqueSkus = _maxUniqueSkus
        };

        await _pipeline.ExecuteAsync(context, ct);
        // counters.OrdersProcessed = 1;

        await dbContext.SaveChangesAsync(ct);

        await AssignWorkersAsync(context, ct);

        return new GenerateShortPickListsResult(
            order.OrderUuid,
            counters.ToResult(),
            context.PickLists.Select(p => p.PickListUuid).ToList());
    }

    public async Task<GenerateSortItemsPickListsBatchResult> GenerateSortItemsPickAsync(
        IReadOnlyList<WmsOrder> orders, IReadOnlyList<WmsOrderItem> sortOrderItems, CancellationToken ct = default)
    {
        var counters = new PickListGenerationCounters();
        if (orders.Count == 0 || sortOrderItems.Count == 0)
            return new GenerateSortItemsPickListsBatchResult(counters.ToResult(), []);

        var now = DateTime.UtcNow;

        var context = new PickListGenerationContext
        {
            Orders = orders,
            Now = now,
            Counters = counters,
            WorkerIndex = 0,
            SortOrderItems = sortOrderItems,
            FixedPriority = 0,
            UseRouteOnlyDropZone = true,
            MaxUniqueSkus = _maxUniqueSkus
        };

        await _pipeline.ExecuteAsync(context, ct);
        counters.OrdersProcessed = orders.Count;

        await dbContext.SaveChangesAsync(ct);

        await AssignWorkersAsync(context, ct);

        return new GenerateSortItemsPickListsBatchResult(
            counters.ToResult(),
            context.PickLists.Select(p => p.PickListUuid).ToList());
    }

    public async Task<GenerateSortItemsPickListsResult> GenerateSortItemsPickAsync(WmsOrder order, IReadOnlyList<WmsOrderItem> sortOrderItems, CancellationToken ct = default)
    {
        var batchResult = await GenerateSortItemsPickAsync([order], sortOrderItems, ct);
        return new GenerateSortItemsPickListsResult(order.OrderUuid, batchResult.GenerationResult, batchResult.PickListUuids);
    }

    /// <summary>
    /// Assigns a worker to each generated pick list after the generation transaction has committed.
    /// Runs outside the generation transaction so task creation isn't nested inside it.
    /// </summary>
    private async Task AssignWorkersAsync(PickListGenerationContext context, CancellationToken ct)
    {
        if (context.PickLists.Count == 0)
            return;

        var workers = await _assignWorkerStep.LoadAssignableWorkersAsync(context, ct);
       
        foreach (var pickList in context.PickLists)
        {
            try
            {
                await _assignWorkerStep.AssignWorkerAsync(context, pickList, workers, ct);
                await dbContext.SaveChangesAsync(ct);
            }
            catch (Exception)
            {
                // Ignore any worker assignment failures. Unassigned pick lists stay PENDING and
                // can be picked up by a later assignment pass.
            }
        }
    }

    private static PickListGenerationStepBase BuildPipeline(
        WmsDbContextWrite dbContext,
        OrderInventoryAllocationService allocationService)
    {
        var step1 = new Step1LoadExistingAllocations(dbContext, allocationService);
        var step1Validate = new Step1ValidateFromBins(dbContext);
        var step2 = new Step2SetPriority(dbContext);
        var step3 = new Step3SelectDropZone(dbContext);
        var step4 = new Step4SortAllocationLines(dbContext);
        var step5 = new Step5BreakIntoPickLists(dbContext);

        step1.ChainTo(step1Validate).ChainTo(step2).ChainTo(step3).ChainTo(step4).ChainTo(step5);
        return step1;
    }
}

public sealed record GenerateShortPickListsResult(
    Guid OrderUuid,
    Command.GeneratePickListsResult GenerationResult,
    IReadOnlyList<Guid> PickListUuids);

public sealed record GenerateSortItemsPickListsResult(
    Guid OrderUuid,
    Command.GeneratePickListsResult GenerationResult,
    IReadOnlyList<Guid> PickListUuids);

public sealed record GenerateSortItemsPickListsBatchResult(
    Command.GeneratePickListsResult GenerationResult,
    IReadOnlyList<Guid> PickListUuids);
