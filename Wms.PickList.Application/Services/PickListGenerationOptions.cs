namespace Wms.PickList.Application.Services;

public sealed class PickListGenerationOptions
{
    public const int ClusterOrderPriority = 5;

    public int? FixedPriority { get; init; }

    public bool UseRouteOnlyDropZone { get; init; }

    public int WorkerIndex { get; init; }
}

public sealed record PickListGenerationRunResult(
    Command.GeneratePickListsResult Result,
    int NextWorkerIndex);
