using SignalAtlas.Domain;

namespace SignalAtlas.Collector;

/// <summary>
/// Generates a fixed number of deterministic IQ blocks (a pure sinusoid at a chosen
/// normalized tone bin) with zero hardware — the CI signal generator (SPEC §4.1, §12.2).
/// </summary>
public sealed class SyntheticSampleSource : ISampleSource
{
    private readonly int _blockCount;
    private readonly int _samplesPerBlock;
    private readonly long _centerFreqHz;
    private readonly int _sampleRateHz;
    private readonly double _amplitude;

    public SyntheticSampleSource(int blockCount, int samplesPerBlock, long centerFreqHz, int sampleRateHz, double amplitude = 0.5)
    {
        _blockCount = blockCount;
        _samplesPerBlock = samplesPerBlock;
        _centerFreqHz = centerFreqHz;
        _sampleRateHz = sampleRateHz;
        _amplitude = amplitude;
    }

    public IEnumerable<IqBlock> Blocks()
    {
        // A deterministic complex tone at 1/8 of the sample rate — same output every run (P5).
        const double toneBinFraction = 0.125;
        for (int b = 0; b < _blockCount; b++)
        {
            var i = new float[_samplesPerBlock];
            var q = new float[_samplesPerBlock];
            for (int s = 0; s < _samplesPerBlock; s++)
            {
                double phase = 2.0 * Math.PI * toneBinFraction * (b * _samplesPerBlock + s);
                i[s] = (float)(_amplitude * Math.Cos(phase));
                q[s] = (float)(_amplitude * Math.Sin(phase));
            }
            yield return new IqBlock(_centerFreqHz, _sampleRateHz, i, q);
        }
    }
}
