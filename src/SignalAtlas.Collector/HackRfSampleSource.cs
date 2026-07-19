using SignalAtlas.Domain;

namespace SignalAtlas.Collector;

/// <summary>
/// RECEIVE-ONLY <see cref="ISampleSource"/> backed by a real <see cref="IHackRfDevice"/> (SPEC §4.1).
/// Reads interleaved signed-8-bit I/Q from the device and normalizes to [-1, 1) with the SAME
/// convention as <see cref="FileSampleSource"/> (i/128f). If no HackRF is available it yields
/// nothing, so the collector falls back to the file/synthetic source (offline-first, SPEC §8.1).
/// The transmit path is never touched — the receive-only invariant (SPEC §4.2 L1) is sacred.
/// </summary>
public sealed class HackRfSampleSource : ISampleSource
{
    private readonly IHackRfDevice _device;
    private readonly long _centerFreqHz;
    private readonly int _sampleRateHz;
    private readonly RxGain _gain;
    private readonly int _basebandBwHz;
    private readonly bool _biasTee;
    private readonly int _samplesPerBlock;

    public HackRfSampleSource(
        IHackRfDevice device, long centerFreqHz, int sampleRateHz, RxGain gain, int basebandBwHz,
        bool biasTee, int samplesPerBlock)
    {
        if (samplesPerBlock <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerBlock));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _centerFreqHz = centerFreqHz;
        _sampleRateHz = sampleRateHz;
        _gain = gain;
        _basebandBwHz = basebandBwHz;
        _biasTee = biasTee;
        _samplesPerBlock = samplesPerBlock;
    }

    public IEnumerable<IqBlock> Blocks()
    {
        if (!_device.IsAvailable)
            yield break; // Graceful fallback — no hardware, no blocks (SPEC §8.1).

        _device.OpenReceive(_centerFreqHz, _sampleRateHz, _gain, _basebandBwHz, _biasTee);
        try
        {
            while (true)
            {
                var buffer = _device.ReadBlock();
                if (buffer.IsEmpty)
                    yield break; // End-of-stream.

                var span = buffer.Span;
                int sampleCount = span.Length / 2;
                var i = new float[sampleCount];
                var q = new float[sampleCount];
                for (int s = 0; s < sampleCount; s++)
                {
                    int idx = s * 2;
                    i[s] = (sbyte)span[idx] / 128f;      // same normalization as FileSampleSource
                    q[s] = (sbyte)span[idx + 1] / 128f;
                }
                yield return new IqBlock(_centerFreqHz, _sampleRateHz, i, q);
            }
        }
        finally
        {
            _device.Close();
        }
    }
}
