using Wms.Persistence.DbContexts;

namespace Wms.PickList.Application.Services.Generation;

internal abstract class PickListGenerationStepBase(WmsDbContextWrite dbContext)
{
    private PickListGenerationStepBase? _nextStep;

    protected WmsDbContextWrite DbContext => dbContext;

    public PickListGenerationStepBase ChainTo(PickListGenerationStepBase next)
    {
        _nextStep = next;
        return next;
    }

    public async Task ExecuteAsync(PickListGenerationContext context, CancellationToken ct)
    {
        await ExecuteCoreAsync(context, ct);
        if (_nextStep is not null)
            await _nextStep.ExecuteAsync(context, ct);
    }

    protected abstract Task ExecuteCoreAsync(PickListGenerationContext context, CancellationToken ct);
}
