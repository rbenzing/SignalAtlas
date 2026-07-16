using SignalAtlas.Domain;

namespace SignalAtlas.Api;

/// <summary>
/// Wire DTO for one waterfall frame (SPEC §9.2 /spectrum/frames, §9.3 spectrum.frame). Bins are
/// downsampled to <= <see cref="SpectrumSupport.MaxBins"/> to bound the payload.
/// </summary>
public sealed record SpectrumFrameDto(
    DateTimeOffset Time,
    long CenterFreqHz,
    int SampleRateHz,
    double[] PowerDbfs);

/// <summary>
/// Occupancy-vs-frequency snapshot from the most recent frame (SPEC §9.2 /spectrum/occupancy, §8.2).
/// <see cref="OccupiedFraction"/> is the share of bins at/above noise-floor + 6 dB (§8.2 threshold).
/// </summary>
public sealed record OccupancyDto(
    long CenterFreqHz,
    int SampleRateHz,
    long[] FreqHz,
    double[] PowerDbfs,
    double ThresholdDbfs,
    double OccupiedFraction);

/// <summary>Per-band coverage/last-seen indicator (SPEC §9.2 /spectrum/coverage, §4.4).</summary>
public sealed record CoverageBandDto(
    string Key,
    string Label,
    long LowHz,
    long HighHz,
    DateTimeOffset? LastSeen,
    double? AgeSeconds,
    bool Covered);

/// <summary>
/// Shared spectrum read-model helpers (SPEC §8.2 / §4.4). Pure + deterministic given inputs.
/// </summary>
public static class SpectrumSupport
{
    /// <summary>Max bins per returned frame — bounds the waterfall payload (SPEC §8.2).</summary>
    public const int MaxBins = 256;

    /// <summary>Occupancy threshold above the median noise floor (SPEC §8.2 = noise + 6 dB).</summary>
    public const double OccupancyMarginDb = 6.0;

    /// <summary>Priority scan bands whose coverage/last-seen is surfaced (SPEC §4.4).</summary>
    public static readonly IReadOnlyList<CoverageBand> Bands =
    [
        new("adsb-1090", "ADS-B 1090 MHz", 1_087_000_000, 1_093_000_000),
        new("ism-2400", "Wi-Fi / BLE / Zigbee 2.4 GHz", 2_400_000_000, 2_485_000_000),
        new("ism-sub-ghz", "ISM 902-928 & 433 MHz", 433_050_000, 928_000_000),
        new("fm-broadcast", "FM broadcast 88-108 MHz", 88_000_000, 108_000_000),
    ];

    /// <summary>A priority band's key/label + inclusive frequency window.</summary>
    public sealed record CoverageBand(string Key, string Label, long LowHz, long HighHz)
    {
        public bool Contains(long freqHz) => freqHz >= LowHz && freqHz <= HighHz;
    }

    /// <summary>
    /// Peak-preserving (max-hold) downsample of a PSD bin array to at most <see cref="MaxBins"/>
    /// bins. Max-hold keeps narrow signal peaks visible in the waterfall (SPEC §8.2).
    /// </summary>
    public static double[] Downsample(double[] bins, int maxBins = MaxBins)
    {
        if (bins.Length <= maxBins)
            return (double[])bins.Clone();

        int group = (int)Math.Ceiling(bins.Length / (double)maxBins);
        int outLen = (int)Math.Ceiling(bins.Length / (double)group);
        var outBins = new double[outLen];
        for (int o = 0; o < outLen; o++)
        {
            double max = double.NegativeInfinity;
            int start = o * group;
            int end = Math.Min(start + group, bins.Length);
            for (int i = start; i < end; i++)
                if (bins[i] > max) max = bins[i];
            outBins[o] = max;
        }
        return outBins;
    }

    /// <summary>Center frequency of bin <paramref name="i"/> (SPEC §8.2 bin geometry).</summary>
    public static long BinFreqHz(long centerFreqHz, int sampleRateHz, int binCount, int i) =>
        centerFreqHz + (long)Math.Round((i - binCount / 2.0) * ((double)sampleRateHz / binCount));

    /// <summary>Median of a copy of <paramref name="values"/> (noise-floor estimate, SPEC §8.2).</summary>
    public static double Median(double[] values)
    {
        if (values.Length == 0) return 0.0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
