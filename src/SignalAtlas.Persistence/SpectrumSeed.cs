using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Synthetic PSD frames seeded at startup (SPEC §4.3 offline-first parity) so the Spectrum
/// waterfall / occupancy / coverage views render WITHOUT running live ingestion. Deterministic:
/// a raised noise floor with a couple of band-centered peaks across priority bands (§4.4).
/// </summary>
public static class SpectrumSeed
{
    private static readonly DateTimeOffset Anchor = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);
    private const int Bins = 256;
    private const int SampleRateHz = 2_000_000;

    /// <summary>A handful of frames alternating between the 915 MHz ISM and 2.44 GHz bands.</summary>
    public static IEnumerable<SpectrumFrame> Frames()
    {
        long[] centers = [915_000_000, 2_440_000_000];
        for (int t = 0; t < 12; t++)
        {
            long center = centers[t % centers.Length];
            yield return new SpectrumFrame(
                Time: Anchor.AddSeconds(t),
                CenterFreqHz: center,
                SampleRateHz: SampleRateHz,
                PowerDbfs: SyntheticBins(t));
        }
    }

    // Noise floor ~ -95 dBFS with a peak that drifts across the band each frame.
    private static double[] SyntheticBins(int t)
    {
        var bins = new double[Bins];
        int peak = (t * 7 + 40) % Bins;
        for (int i = 0; i < Bins; i++)
        {
            double distance = Math.Abs(i - peak);
            double lobe = distance <= 6 ? (30.0 * (1.0 - distance / 6.0)) : 0.0;
            bins[i] = -95.0 + lobe;
        }
        return bins;
    }
}
