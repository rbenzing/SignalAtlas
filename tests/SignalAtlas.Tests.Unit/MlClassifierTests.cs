using SignalAtlas.Domain;
using SignalAtlas.Ml;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M9 — the softmax <see cref="MlClassifier"/> implements the SAME <see cref="IClassifier"/>
/// contract as the rule scorer (SPEC §8.9). Invariants: valid protocol, confidence ∈ [0,1] and
/// NON-EMPTY attribution evidence (AC-CL1, P4, §7.3).
/// </summary>
public class MlClassifierTests
{
    // Train-on-construction from a fixed seed keeps the classifier deterministic and self-contained.
    private static readonly MlClassifier Classifier = new();

    private static FeatureVector LoRa() =>
        new(CenterFreqHz: 915_000_000, Bandwidth3dBHz: 125_000, Bandwidth20dBHz: 250_000,
            PeakPowerDbfs: -40, SnrDb: 12, DurationMs: 60, DutyCycle: 0.1, ModulationHint: "CSS");

    private static FeatureVector AdsB() =>
        new(CenterFreqHz: 1_090_000_000, Bandwidth3dBHz: 2_000_000, Bandwidth20dBHz: 4_000_000,
            PeakPowerDbfs: -30, SnrDb: 20, DurationMs: 1, DutyCycle: 0.01, ModulationHint: "PPM");

    [Fact]
    public void Classify_LoRaShape_YieldsLoRaWithConfidenceAndEvidence()
    {
        var r = Classifier.Classify(LoRa());
        Assert.Equal("LoRa", r.Protocol);
        Assert.Equal(MlClassifier.ClassifierName, r.Classifier);
        Assert.InRange(r.Confidence, 0.0, 1.0);
        Assert.NotEmpty(r.Evidence); // attribution evidence (feature × weight contributions)
    }

    [Fact]
    public void Classify_AdsBShape_YieldsAdsBWithConfidenceAndEvidence()
    {
        var r = Classifier.Classify(AdsB());
        Assert.Equal("ADS-B", r.Protocol);
        Assert.InRange(r.Confidence, 0.0, 1.0);
        Assert.NotEmpty(r.Evidence);
    }

    [Fact]
    public void Classify_EvidenceReferencesRealFeatureNames()
    {
        var r = Classifier.Classify(LoRa());
        Assert.All(r.Evidence, e => Assert.Contains(e.Feature, FeatureEncoder.FeatureNames));
    }

    [Fact]
    public void Classify_IsDeterministic()
    {
        var a = Classifier.Classify(LoRa());
        var b = Classifier.Classify(LoRa());
        Assert.Equal(a.Protocol, b.Protocol);
        Assert.Equal(a.Confidence, b.Confidence);
    }
}
