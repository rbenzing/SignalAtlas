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

    public BrowserUploadSampleSource(int capacity, int samplesPerBlock)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (samplesPerBlock <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerBlock));
        _samplesPerBlock = samplesPerBlock;
        _channel = Channel.CreateBounded<IqBlock>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Enqueue one WebSocket binary frame of interleaved signed-8-bit I/Q.</summary>
    public void Enqueue(ReadOnlyMemory<byte> int8Iq, long centerFreqHz, int sampleRateHz)
    {
        var span = int8Iq.Span;
        int totalSamples = span.Length / 2;
        int fullBlocks = totalSamples / _samplesPerBlock;

        for (int b = 0; b < fullBlocks; b++)
        {
            var i = new float[_samplesPerBlock];
            var q = new float[_samplesPerBlock];
            for (int s = 0; s < _samplesPerBlock; s++)
            {
                int idx = (b * _samplesPerBlock + s) * 2;
                i[s] = (sbyte)span[idx] / 128f;
                q[s] = (sbyte)span[idx + 1] / 128f;
            }
            _channel.Writer.TryWrite(new IqBlock(centerFreqHz, sampleRateHz, i, q));
        }
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
                hasData = reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult();
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
