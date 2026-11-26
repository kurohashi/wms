using Wms.Persistence.DbContexts;
using Task = System.Threading.Tasks.Task;

namespace Wms.PickList.Application.Services.Generation;

/// <summary>
/// Step 2: Set priority at order item level using existing order rules.
/// </summary>
internal sealed class Step2SetPriority(WmsDbContextWrite dbContext)
    : PickListGenerationStepBase(dbContext)
{
    protected override Task ExecuteCoreAsync(PickListGenerationContext context, CancellationToken ct)
    {
        foreach (var draft in context.OrderItemDrafts)
        {
            draft.Priority = PickListPriorityResolver.Resolve(draft.Order, context.FixedPriority);
        }

        return Task.CompletedTask;
    }
}
