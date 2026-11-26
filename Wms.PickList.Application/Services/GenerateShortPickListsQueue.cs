using System.Threading.Channels;

namespace Wms.PickList.Application.Services;

internal sealed record GenerateShortPickListJob(
    Guid RouteUuid,
    long OrderId,
    IReadOnlyList<long> OrderItemIds);

internal sealed class GenerateShortPickListsQueue : IGenerateShortPickListsQueue
{
    private readonly Channel<GenerateShortPickListJob> _channel = Channel.CreateUnbounded<GenerateShortPickListJob>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<GenerateShortPickListJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(
        Guid routeUuid,
        long orderId,
        IReadOnlyList<long> orderItemIds,
        CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(new GenerateShortPickListJob(routeUuid, orderId, orderItemIds), ct);
}
