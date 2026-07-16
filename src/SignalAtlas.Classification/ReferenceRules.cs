using SignalAtlas.Domain;

namespace SignalAtlas.Classification;

/// <summary>
/// Reference weighted rules for the priority bands (SPEC §4 reference bands, §8.3): LoRa, BLE,
/// Wi-Fi, ADS-B and FM. Each rule weights three features — center frequency, −3 dB bandwidth and
/// modulation hint — summing to 1.0 so a rule's normalized score is the matched weight directly.
/// Weights favour the modulation hint (0.4) as the most discriminating feature, then band (0.3)
/// and bandwidth (0.3). Rules are intentionally readable and every match is cited (P4, §7.3).
/// </summary>
public static class ReferenceRules
{
    private const double BandWeight = 0.30;
    private const double BandwidthWeight = 0.30;
    private const double ModulationWeight = 0.40;

    /// <summary>The canonical rule set used by the default <see cref="RuleBasedClassifier"/>.</summary>
    public static IReadOnlyList<ProtocolRule> Default { get; } = new[]
    {
        // LoRa — CSS in the 433 MHz or 902–928 MHz ISM bands, 125/250/500 kHz channels.
        new ProtocolRule("LoRa",
            Band("center_freq_hz", "433 MHz or 902–928 MHz ISM", BandWeight,
                f => InRange(f.CenterFreqHz, 433_050_000, 434_790_000)
                     || InRange(f.CenterFreqHz, 902_000_000, 928_000_000)),
            OneOfBandwidth("bandwidth_3db_hz", "125/250/500 kHz CSS channel", BandwidthWeight,
                new long[] { 125_000, 250_000, 500_000 }, 0.10),
            Modulation("CSS")),

        // BLE — GFSK, 2.4 GHz, ~1–2 MHz channel.
        new ProtocolRule("BLE",
            Band("center_freq_hz", "2.400–2.485 GHz ISM", BandWeight,
                f => InRange(f.CenterFreqHz, 2_400_000_000, 2_485_000_000)),
            BandwidthBetween("bandwidth_3db_hz", "~1–2 MHz", BandwidthWeight, 800_000, 2_200_000),
            Modulation("GFSK")),

        // Wi-Fi — legacy OFDM, 2.4 GHz, ~20 MHz channel (§8.4 Tier B: 20 MHz only).
        new ProtocolRule("Wi-Fi",
            Band("center_freq_hz", "2.400–2.485 GHz ISM", BandWeight,
                f => InRange(f.CenterFreqHz, 2_400_000_000, 2_485_000_000)),
            BandwidthBetween("bandwidth_3db_hz", "~20 MHz channel", BandwidthWeight, 16_000_000, 24_000_000),
            Modulation("OFDM")),

        // ADS-B — PPM at ~1090 MHz, ~2 MHz occupied bandwidth.
        new ProtocolRule("ADS-B",
            Band("center_freq_hz", "~1090 MHz", BandWeight,
                f => InRange(f.CenterFreqHz, 1_089_000_000, 1_091_000_000)),
            BandwidthBetween("bandwidth_3db_hz", "~2 MHz", BandwidthWeight, 1_500_000, 2_500_000),
            Modulation("PPM")),

        // FM broadcast — wideband (~200 kHz) in the 88–108 MHz band.
        new ProtocolRule("FM",
            Band("center_freq_hz", "88–108 MHz broadcast", BandWeight,
                f => InRange(f.CenterFreqHz, 88_000_000, 108_000_000)),
            BandwidthBetween("bandwidth_3db_hz", "~200 kHz wideband", BandwidthWeight, 150_000, 260_000),
            Modulation("FM")),
    };

    private static bool InRange(long value, long lo, long hi) => value >= lo && value <= hi;

    private static Criterion Band(string feature, string expectation, double weight, Func<FeatureVector, bool> match) =>
        new(feature, expectation, weight, f => (match(f), $"{f.CenterFreqHz / 1e6:0.###} MHz"));

    private static Criterion BandwidthBetween(string feature, string expectation, double weight, long lo, long hi) =>
        new(feature, expectation, weight,
            f => (f.Bandwidth3dBHz >= lo && f.Bandwidth3dBHz <= hi, $"{f.Bandwidth3dBHz / 1e3:0.###} kHz"));

    private static Criterion OneOfBandwidth(string feature, string expectation, double weight, long[] channels, double tolerance) =>
        new(feature, expectation, weight,
            f => (channels.Any(ch => Math.Abs(f.Bandwidth3dBHz - ch) <= ch * tolerance), $"{f.Bandwidth3dBHz / 1e3:0.###} kHz"));

    private static Criterion Modulation(string hint) =>
        new("modulation_hint", $"'{hint}'", ModulationWeight,
            f => (string.Equals(f.ModulationHint, hint, StringComparison.OrdinalIgnoreCase),
                  f.ModulationHint ?? "(none)"));
}
