using SignalAtlas.Classification;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M2 — rule-based Classification Engine (SPEC §8.3). Invariants under test:
/// AC-CL1 (evidence non-empty + confidence ∈ [0,1]) and AC-CL3 (no-match → Unknown).
/// </summary>
public class ClassificationTests
{
    private static readonly RuleBasedClassifier Classifier = new();

    // Shaped feature vectors matching the reference bands (SPEC §4 / §8.3).
    private static FeatureVector LoRa() =>
        new(CenterFreqHz: 915_000_000, Bandwidth3dBHz: 125_000, Bandwidth20dBHz: 250_000,
            PeakPowerDbfs: -40, SnrDb: 12, DurationMs: 60, DutyCycle: 0.1, ModulationHint: "CSS");

    private static FeatureVector AdsB() =>
        new(CenterFreqHz: 1_090_000_000, Bandwidth3dBHz: 2_000_000, Bandwidth20dBHz: 4_000_000,
            PeakPowerDbfs: -30, SnrDb: 20, DurationMs: 1, DutyCycle: 0.01, ModulationHint: "PPM");

    private static FeatureVector Fm() =>
        new(CenterFreqHz: 98_100_000, Bandwidth3dBHz: 200_000, Bandwidth20dBHz: 250_000,
            PeakPowerDbfs: -20, SnrDb: 30, DurationMs: 1000, DutyCycle: 1.0, ModulationHint: "FM");

    // Sits in the 2.4 GHz band but with a bandwidth belonging to neither BLE nor Wi-Fi,
    // and no modulation hint → at most one criterion matches per rule → below floor.
    private static FeatureVector Ambiguous() =>
        new(CenterFreqHz: 2_450_000_000, Bandwidth3dBHz: 8_000_000, Bandwidth20dBHz: 12_000_000,
            PeakPowerDbfs: -50, SnrDb: 5, DurationMs: 10, DutyCycle: 0.2, ModulationHint: null);

    // 1. LoRa-shaped vector → "LoRa" with non-empty evidence.
    [Fact]
    public void Classify_LoRaShape_YieldsLoRaWithEvidence()
    {
        var result = Classifier.Classify(LoRa());
        Assert.Equal("LoRa", result.Protocol);
        Assert.NotEmpty(result.Evidence);
    }

    // 6. Triangulation — a second protocol proves the scorer generalizes.
    [Fact]
    public void Classify_AdsBShape_YieldsAdsBWithEvidence()
    {
        var result = Classifier.Classify(AdsB());
        Assert.Equal("ADS-B", result.Protocol);
        Assert.NotEmpty(result.Evidence);
    }

    [Fact]
    public void Classify_FmShape_YieldsFm()
    {
        Assert.Equal("FM", Classifier.Classify(Fm()).Protocol);
    }

    // 2. Confidence always ∈ [0,1] across several inputs (AC-CL1).
    [Fact]
    public void Classify_ConfidenceAlwaysWithinUnitInterval()
    {
        foreach (var fv in new[] { LoRa(), AdsB(), Fm(), Ambiguous() })
        {
            var c = Classifier.Classify(fv).Confidence;
            Assert.InRange(c, 0.0, 1.0);
        }
    }

    // 3a. Evidence never empty for a match (AC-CL1).
    [Fact]
    public void Classify_Match_EvidenceNeverEmpty()
    {
        Assert.NotEmpty(Classifier.Classify(LoRa()).Evidence);
    }

    // 3b. Evidence never empty even for Unknown (AC-CL1 + P4).
    [Fact]
    public void Classify_Unknown_EvidenceNeverEmpty()
    {
        Assert.NotEmpty(Classifier.Classify(Ambiguous()).Evidence);
    }

    // 4. Ambiguous / below-floor → Unknown (AC-CL3).
    [Fact]
    public void Classify_Ambiguous_YieldsUnknown()
    {
        Assert.Equal(SignalAtlas.Domain.Classification.Unknown, Classifier.Classify(Ambiguous()).Protocol);
    }

    // 5. Stub classifier keeps the pipeline green (SPEC §8.3 test list).
    [Fact]
    public void AlwaysUnknown_ReturnsUnknownWithEvidence()
    {
        var result = new AlwaysUnknownClassifier().Classify(LoRa());
        Assert.Equal(SignalAtlas.Domain.Classification.Unknown, result.Protocol);
        Assert.NotEmpty(result.Evidence);
        Assert.InRange(result.Confidence, 0.0, 1.0);
    }
}
