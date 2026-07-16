using SignalAtlas.Domain;

namespace SignalAtlas.Ml;

/// <summary>
/// Deterministic synthetic labeled dataset (SPEC §12.2 source 1 — synthetic generators as the
/// primary source for unit/golden work). Draws realistic per-protocol feature distributions
/// (center-freq bands, −3/−20 dB bandwidths, modulation hints, SNR/duration/duty) from a seeded
/// <see cref="DeterministicRng"/> so the same seed yields an identical dataset (P5). Labels are
/// balanced round-robin across the protocol set for a stable macro-F1 eval. A fraction of samples
/// have their modulation hint dropped, forcing the model to also rely on numeric features rather
/// than a trivial one-hot lookup.
/// </summary>
public static class SyntheticDataset
{
    public static readonly string[] Protocols =
        { "LoRa", "BLE", "Wi-Fi", "ADS-B", "FM", "Zigbee", Classification.Unknown };

    /// <summary>Probability a sample's modulation hint is dropped to null (realistic missingness).</summary>
    private const double ModulationDropoutRate = 0.15;

    public static IReadOnlyList<LabeledSample> Generate(int seed, int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        var rng = new DeterministicRng(unchecked((ulong)seed) ^ 0xA5A5A5A5A5A5A5A5UL);
        var samples = new List<LabeledSample>(n);

        for (int i = 0; i < n; i++)
        {
            string label = Protocols[i % Protocols.Length];
            var f = Draw(label, rng);
            samples.Add(new LabeledSample(f, label));
        }

        return samples;
    }

    private static FeatureVector Draw(string label, DeterministicRng r)
    {
        return label switch
        {
            "LoRa" => Lora(r),
            "BLE" => Ble(r),
            "Wi-Fi" => Wifi(r),
            "ADS-B" => AdsB(r),
            "FM" => Fm(r),
            "Zigbee" => Zigbee(r),
            _ => Unknown(r),
        };
    }

    // LoRa — CSS in the 433 MHz or 902–928 MHz ISM band, 125/250/500 kHz channels.
    private static FeatureVector Lora(DeterministicRng r)
    {
        long center = r.NextDouble() < 0.5
            ? (long)r.NextGaussian(433_200_000, 150_000)
            : 902_000_000 + r.NextInt(0, 26_000_000);
        int bw3 = ChannelKhz(r, new[] { 125, 250, 500 }, jitterFrac: 0.06);
        return Make(r, "CSS", center, bw3, bw20Mult: 2.0,
            peak: (-45, 8), snr: (10, 4), durMs: (60, 30), duty: (0.1, 0.05));
    }

    // BLE advertising — GFSK, 2.4 GHz, ~1–2 MHz channel, short bursts.
    private static FeatureVector Ble(DeterministicRng r)
    {
        long center = 2_402_000_000 + r.NextInt(0, 40) * 2_000_000L; // 2402..2480 MHz
        int bw3 = (int)r.NextGaussian(1_200_000, 180_000);
        return Make(r, "GFSK", center, bw3, bw20Mult: 2.4,
            peak: (-55, 8), snr: (8, 4), durMs: (3, 2), duty: (0.02, 0.01));
    }

    // Wi-Fi — legacy 20 MHz OFDM in 2.4 GHz (Tier B: 20 MHz only, §8.4).
    private static FeatureVector Wifi(DeterministicRng r)
    {
        long center = 2_412_000_000 + r.NextInt(0, 11) * 5_000_000L; // channels 1..11
        int bw3 = (int)r.NextGaussian(20_000_000, 1_500_000);
        return Make(r, "OFDM", center, bw3, bw20Mult: 1.15,
            peak: (-50, 8), snr: (15, 5), durMs: (2, 1), duty: (0.30, 0.10));
    }

    // ADS-B — PPM at ~1090 MHz, ~2 MHz occupied, very short squitters.
    private static FeatureVector AdsB(DeterministicRng r)
    {
        long center = (long)r.NextGaussian(1_090_000_000, 150_000);
        int bw3 = (int)r.NextGaussian(2_000_000, 180_000);
        return Make(r, "PPM", center, bw3, bw20Mult: 2.0,
            peak: (-35, 8), snr: (20, 6), durMs: (1, 1), duty: (0.01, 0.005));
    }

    // FM broadcast — wideband (~200 kHz) in 88–108 MHz, continuous.
    private static FeatureVector Fm(DeterministicRng r)
    {
        long center = 88_000_000 + r.NextInt(0, 20_000_000);
        int bw3 = (int)r.NextGaussian(200_000, 18_000);
        return Make(r, "FM", center, bw3, bw20Mult: 1.25,
            peak: (-25, 8), snr: (30, 8), durMs: (1000, 200), duty: (1.0, 0.02));
    }

    // Zigbee/802.15.4 — O-QPSK, 2.4 GHz channels spaced 5 MHz, ~2 MHz occupied.
    private static FeatureVector Zigbee(DeterministicRng r)
    {
        long center = 2_405_000_000 + r.NextInt(0, 16) * 5_000_000L; // channels 11..26
        int bw3 = (int)r.NextGaussian(2_000_000, 150_000);
        return Make(r, "O-QPSK", center, bw3, bw20Mult: 1.5,
            peak: (-55, 8), snr: (10, 4), durMs: (4, 2), duty: (0.05, 0.02));
    }

    // Unknown — junk in the band gaps with random, mismatched features and no modulation hint.
    private static FeatureVector Unknown(DeterministicRng r)
    {
        long[] gapLo = { 150_000_000, 1_200_000_000, 2_500_000_000, 3_000_000_000 };
        long[] gapHi = { 800_000_000, 1_800_000_000, 2_700_000_000, 5_000_000_000 };
        int g = r.NextInt(0, gapLo.Length);
        long center = gapLo[g] + (long)(r.NextDouble() * (gapHi[g] - gapLo[g]));
        int bw3 = 10_000 + r.NextInt(0, 40_000_000);
        int bw20 = (int)(bw3 * (1.5 + r.NextDouble() * 1.5));
        return new FeatureVector(
            CenterFreqHz: center,
            Bandwidth3dBHz: bw3,
            Bandwidth20dBHz: bw20,
            PeakPowerDbfs: -60 + r.NextDouble() * 50,
            SnrDb: -2 + r.NextDouble() * 42,
            DurationMs: 1 + r.NextInt(0, 2000),
            DutyCycle: r.NextDouble(),
            ModulationHint: null);
    }

    private static FeatureVector Make(
        DeterministicRng r, string modulation, long center, int bw3, double bw20Mult,
        (double mean, double std) peak, (double mean, double std) snr,
        (double mean, double std) durMs, (double mean, double std) duty)
    {
        bw3 = Math.Max(1_000, bw3);
        int bw20 = Math.Max(bw3 + 1_000, (int)(bw3 * bw20Mult * (1.0 + 0.05 * r.NextGaussian())));
        string? mod = r.NextDouble() < ModulationDropoutRate ? null : modulation;
        return new FeatureVector(
            CenterFreqHz: Math.Max(1, center),
            Bandwidth3dBHz: bw3,
            Bandwidth20dBHz: bw20,
            PeakPowerDbfs: r.NextGaussian(peak.mean, peak.std),
            SnrDb: r.NextGaussian(snr.mean, snr.std),
            DurationMs: Math.Max(1, (int)r.NextGaussian(durMs.mean, durMs.std)),
            DutyCycle: Math.Clamp(r.NextGaussian(duty.mean, duty.std), 0.0, 1.0),
            ModulationHint: mod);
    }

    private static int ChannelKhz(DeterministicRng r, int[] channelsKhz, double jitterFrac)
    {
        int ch = channelsKhz[r.NextInt(0, channelsKhz.Length)] * 1000;
        return (int)(ch * (1.0 + jitterFrac * r.NextGaussian()));
    }
}
