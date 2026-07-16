using SignalAtlas.Domain;

namespace SignalAtlas.Collector;

/// <summary>
/// Replays interleaved signed-8-bit I/Q (HackRF native format) from an in-memory buffer
/// as ordered <see cref="IqBlock"/>s. Deterministic: same bytes → identical block sequence
/// (SPEC §4.1, §6.1 P5). Zero hardware required for CI.
/// </summary>
public sealed class FileSampleSource : ISampleSource
{
    private readonly ReadOnlyMemory<byte> _raw;
    private readonly int _samplesPerBlock;
    private readonly long _centerFreqHz;
    private readonly int _sampleRateHz;

    public FileSampleSource(ReadOnlyMemory<byte> raw, int samplesPerBlock, long centerFreqHz, int sampleRateHz)
    {
        if (samplesPerBlock <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerBlock));
        _raw = raw;
        _samplesPerBlock = samplesPerBlock;
        _centerFreqHz = centerFreqHz;
        _sampleRateHz = sampleRateHz;
    }

    public IEnumerable<IqBlock> Blocks()
    {
        int totalSamples = _raw.Length / 2;
        int fullBlocks = totalSamples / _samplesPerBlock;

        for (int b = 0; b < fullBlocks; b++)
        {
            var i = new float[_samplesPerBlock];
            var q = new float[_samplesPerBlock];
            var span = _raw.Span;
            for (int s = 0; s < _samplesPerBlock; s++)
            {
                int idx = (b * _samplesPerBlock + s) * 2;
                i[s] = (sbyte)span[idx] / 128f;
                q[s] = (sbyte)span[idx + 1] / 128f;
            }
            yield return new IqBlock(_centerFreqHz, _sampleRateHz, i, q);
        }
    }
}
