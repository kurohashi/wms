using Microsoft.EntityFrameworkCore;
using Wms.Persistence.DbContexts;
using Task = System.Threading.Tasks.Task;

namespace Wms.PickList.Application.Services.Generation;

/// <summary>
/// Filters out order items allocated from route final zone bins
/// (IsRoutable with no linked storage type). Other items continue through the pipeline.
/// </summary>
internal sealed class Step1ValidateFromBins(WmsDbContextWrite dbContext)
    : PickListGenerationStepBase(dbContext)
{
    protected override async Task ExecuteCoreAsync(PickListGenerationContext context, CancellationToken ct)
    {
        if (context.OrderItemDrafts.Count == 0)
            return;

        var fromBinIds = context.OrderItemDrafts
            .SelectMany(d => d.BinAllocations)
            .Select(b => b.BinId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (fromBinIds.Count == 0)
            return;

        var routeFinalZoneBinIds = await DbContext.Bins
            .AsNoTracking()
            .Where(b => fromBinIds.Contains(b.Id)
                        && b.IsRoutable
                        && b.StorageTypeUuid == null
                        && b.IsActive == true
                        && b.IsDeleted == false)
            .Select(b => b.Id)
            .ToHashSetAsync(ct);

        if (routeFinalZoneBinIds.Count == 0)
            return;

        foreach (var draft in context.OrderItemDrafts)
        {
            draft.BinAllocations.RemoveAll(b => routeFinalZoneBinIds.Contains(b.BinId));
        }

        var removedDraftCount = context.OrderItemDrafts.RemoveAll(d => d.BinAllocations.Count == 0);
        context.Counters.ItemsSkippedRouteFinalZone += removedDraftCount;
    }
}
