using System.Threading.Channels;
using SignalAtlas.Domain;

namespace SignalAtlas.Pipeline;

/// <summary>
/// Bounded producer/consumer queue between the sample source (producer) and the ingestion loop
/// (consumer) (SPEC §4.9 G27, NFR-T1/NFR-C3). Backed by a bounded
/// <see cref="System.Threading.Channels.Channel{T}"/> with <see cref="BoundedChannelFullMode.DropOldest"/>:
/// on saturation the OLDEST block is dropped so growth is never unbounded, and every drop is COUNTED
/// (<see cref="Drops"/> → the <c>drops_total</c> metric) — never a silent loss.
/// </summary>
public sealed class BoundedBlockBuffer
{
    private readonly Channel<IqBlock> _channel;
    private long _drops;

    public BoundedBlockBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _channel = Channel.CreateBounded<IqBlock>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            itemDropped: _ => Interlocked.Increment(ref _drops));
    }

    /// <summary>The documented, fixed queue depth (bounded ring ≤ N, NFR-C3).</summary>
    public int Capacity { get; }

    /// <summary>Count of blocks dropped due to saturation (drop-oldest policy) — the <c>drops_total</c> metric.</summary>
    public long Drops => Interlocked.Read(ref _drops);

    public ChannelReader<IqBlock> Reader => _channel.Reader;

    /// <summary>Offer a block. With drop-oldest this always accepts; a displaced oldest block is counted.</summary>
    public bool TryWrite(IqBlock block) => _channel.Writer.TryWrite(block);

    /// <summary>Signal no more blocks will be produced (unblocks a draining consumer).</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
