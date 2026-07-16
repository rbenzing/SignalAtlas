using System.Threading.Channels;
using SignalAtlas.Domain;

namespace SignalAtlas.Collector;

/// <summary>
/// Receive-only <see cref="ISampleSource"/> fed by a browser WebUSB HackRF over the /ingest/iq
/// WebSocket. Converts interleaved signed-8-bit I/Q (HackRF native, identical to
/// <see cref="FileSampleSource"/>) into normalized <see cref="IqBlock"/>s and hands them to the
/// existing pipeline. Bounded drop-oldest: under backpressure the freshest samples win (live RF).
/// Raw IQ is never persisted here — it is consumed into blocks and discarded (egress discipline).
/// Exposes no transmit member (SPEC §4.2 L1).
/// </summary>
public sealed class BrowserUploadSampleSource : ISampleSource
{
    private readonly Channel<IqBlock> _channel;
    private readonly int _samplesPerBlock;
    private byte[] _carry = Array.Empty<byte>();

    public BrowserUploadSampleSource(int capacity, int samplesPerBlock)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (samplesPerBlock <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerBlock));
        _samplesPerBlock = samplesPerBlock;
        _channel = Channel.CreateBounded<IqBlock>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>
    /// Enqueue one WebSocket binary frame of interleaved signed-8-bit I/Q. Leftover bytes that
    /// don't complete a full block are carried over and prepended to the next call's bytes.
    /// Not safe for concurrent callers — single producer per connection (one WebSocket receive
    /// loop per instance).
    /// </summary>
    public void Enqueue(ReadOnlyMemory<byte> int8Iq, long centerFreqHz, int sampleRateHz)
    {
        var span = int8Iq.Span;
        int bytesPerBlock = _samplesPerBlock * 2;

        byte[] combined;
        if (_carry.Length == 0)
        {
            combined = span.ToArray();
        }
        else
        {
            combined = new byte[_carry.Length + span.Length];
            _carry.CopyTo(combined, 0);
            span.CopyTo(combined.AsSpan(_carry.Length));
        }

        int fullBlocks = combined.Length / bytesPerBlock;
        int consumed = fullBlocks * bytesPerBlock;

        for (int b = 0; b < fullBlocks; b++)
        {
            var i = new float[_samplesPerBlock];
            var q = new float[_samplesPerBlock];
            for (int s = 0; s < _samplesPerBlock; s++)
            {
                int idx = (b * _samplesPerBlock + s) * 2;
                i[s] = (sbyte)combined[idx] / 128f;
                q[s] = (sbyte)combined[idx + 1] / 128f;
            }
            _channel.Writer.TryWrite(new IqBlock(centerFreqHz, sampleRateHz, i, q));
        }

        _carry = combined.Length == consumed ? Array.Empty<byte>() : combined[consumed..];
    }

    /// <summary>Signal end-of-stream (WebSocket closed): <see cref="Blocks"/> terminates.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public IEnumerable<IqBlock> Blocks()
    {
        var reader = _channel.Reader;
        while (true)
        {
            bool hasData;
            try
            {
                hasData = reader.WaitToReadAsync().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            if (!hasData)
                yield break;
            while (reader.TryRead(out var block))
                yield return block;
        }
    }
}
