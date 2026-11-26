using System.Threading.Channels;

namespace Wms.PickList.Application.Services;

internal sealed record GenerateSortItemsPickListJob(
    Guid RouteUuid,
    IReadOnlyList<SortItemsPickListOrderRequest> Orders);

internal sealed class GenerateSortItemsPickListsQueue : IGenerateSortItemsPickListsQueue
{
    private readonly Channel<GenerateSortItemsPickListJob> _channel = Channel.CreateUnbounded<GenerateSortItemsPickListJob>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<GenerateSortItemsPickListJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(
        Guid routeUuid,
        IReadOnlyList<SortItemsPickListOrderRequest> orders,
        CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(new GenerateSortItemsPickListJob(routeUuid, orders), ct);
}
