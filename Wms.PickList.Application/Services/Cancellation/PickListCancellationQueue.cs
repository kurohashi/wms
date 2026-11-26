using System.Threading.Channels;

namespace Wms.PickList.Application.Services.Cancellation;

internal sealed class PickListCancellationQueue : IPickListCancellationQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid orderUuid, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(orderUuid, ct);
}
