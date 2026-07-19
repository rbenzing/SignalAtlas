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

    /// <summary>
    /// Builds a tiny degenerate 2-class model where one feature has a 0 std (as a hand-edited or
    /// corrupted artifact might). Zero-fill weights so the finite check exercises the standardize
    /// step specifically.
    /// </summary>
    private static MlModel BuildDegenerateModel()
    {
        int dim = FeatureEncoder.Dimension;
        var means = new double[dim];
        var stds = new double[dim];
        for (int j = 0; j < dim; j++) stds[j] = 1.0;
        stds[0] = 0.0; // degenerate: center_freq_mhz std collapsed to 0

        return new MlModel
        {
            Labels = new[] { "LoRa", "ADS-B" },
            FeatureNames = (string[])FeatureEncoder.FeatureNames.Clone(),
            Means = means,
            Stds = stds,
            Weights = new[] { new double[dim], new double[dim] },
            Bias = new double[2],
        };
    }

    [Fact]
    public void FromJson_ClampsZeroStdOnLoad()
    {
        var json = BuildDegenerateModel().ToJson();
        var loaded = MlModel.FromJson(json);
        Assert.All(loaded.Stds, s => Assert.True(s >= MlModel.MinStd));
    }

    [Fact]
    public void Classify_ZeroStdViaFromJsonRoundTrip_ProducesNoNaNOrInfinity()
    {
        var json = BuildDegenerateModel().ToJson();
        var loaded = MlModel.FromJson(json);
        var classifier = new MlClassifier(loaded);

        var r = classifier.Classify(LoRa());

        Assert.False(double.IsNaN(r.Confidence));
        Assert.False(double.IsInfinity(r.Confidence));
        Assert.InRange(r.Confidence, 0.0, 1.0);
        Assert.Contains(r.Protocol, loaded.Labels);
    }

    [Fact]
    public void Classify_ZeroStdBypassingFromJson_ProducesNoNaNOrInfinity()
    {
        // Constructs a model directly (bypassing MlModel.FromJson's load-time clamp) to prove the
        // defensive belt at the MlClassifier.Classify divide site alone is enough to avoid NaN.
        var degenerate = BuildDegenerateModel();
        var classifier = new MlClassifier(degenerate);

        var r = classifier.Classify(LoRa());

        Assert.False(double.IsNaN(r.Confidence));
        Assert.False(double.IsInfinity(r.Confidence));
        Assert.InRange(r.Confidence, 0.0, 1.0);
        Assert.Contains(r.Protocol, degenerate.Labels);
    }

    [Fact]
    public void Classify_NormalModel_UnchangedByStdGuard()
    {
        // The default (well-formed) model's classification must be identical after the guard was
        // added — its Stds are already >= MlModel.MinStd (trainer clamps to 1e-9 -> 1.0), so
        // Math.Max(std, MinStd) is a no-op.
        var r = Classifier.Classify(LoRa());
        Assert.Equal("LoRa", r.Protocol);
        Assert.False(double.IsNaN(r.Confidence));
    }
}
