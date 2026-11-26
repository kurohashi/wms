using System.Threading.Channels;

namespace Wms.PickList.Application.Services;

internal sealed record GeneratePickListsJob(IReadOnlyList<long> OrderIds);

internal sealed class GeneratePickListsQueue : IGeneratePickListsQueue
{
    private readonly Channel<GeneratePickListsJob> _channel = Channel.CreateUnbounded<GeneratePickListsJob>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<GeneratePickListsJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(IReadOnlyList<long> orderIds, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(new GeneratePickListsJob(orderIds), ct);
}
