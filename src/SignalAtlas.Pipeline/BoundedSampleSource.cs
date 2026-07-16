using System.Threading.Channels;
using SignalAtlas.Domain;

namespace SignalAtlas.Pipeline;

/// <summary>
/// Wraps an inner <see cref="ISampleSource"/> in a bounded producer/consumer queue (SPEC §4.9 G27):
/// the inner source is drained on a background producer task into a <see cref="BoundedBlockBuffer"/>
/// (drop-oldest, counted), and <see cref="Blocks"/> yields to the consumer at its own pace. When the
/// consumer can't keep up, the OLDEST block is dropped and <see cref="Drops"/> increments — bounded,
/// never silent (NFR-T1/NFR-C3). Receive-only: no transmit member is introduced (SPEC §4.2 L1).
/// </summary>
public sealed class BoundedSampleSource : ISampleSource
{
    private readonly ISampleSource _inner;
    private readonly BoundedBlockBuffer _buffer;

    public BoundedSampleSource(ISampleSource inner, int capacity)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _buffer = new BoundedBlockBuffer(capacity);
    }

    public int Capacity => _buffer.Capacity;

    /// <summary>Blocks dropped under backpressure so far (the <c>drops_total</c> metric).</summary>
    public long Drops => _buffer.Drops;

    public IEnumerable<IqBlock> Blocks()
    {
        var producer = Task.Run(() =>
        {
            try
            {
                foreach (var block in _inner.Blocks())
                    _buffer.TryWrite(block); // drop-oldest never blocks the fast producer.
            }
            finally
            {
                _buffer.Complete();
            }
        });

        var reader = _buffer.Reader;
        while (true)
        {
            IqBlock block;
            try
            {
                block = reader.ReadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (ChannelClosedException)
            {
                break; // producer finished and the queue drained.
            }
            yield return block;
        }

        producer.GetAwaiter().GetResult(); // surface any producer fault after draining.
    }
}
