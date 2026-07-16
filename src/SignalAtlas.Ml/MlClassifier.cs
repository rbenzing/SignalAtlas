using SignalAtlas.Domain;

namespace SignalAtlas.Ml;

// NOTE: this assembly's namespace ends in ".Ml", so the unqualified name "Classification" still
// resolves to SignalAtlas.Domain.Classification. We fully-qualify the result type anyway for parity
// with the rule-scorer project and to be explicit at the seam.

/// <summary>
/// ML protocol classifier — a softmax model behind the SAME <see cref="IClassifier"/> contract as
/// the rule scorer, so it is hot-swappable (SPEC §8.9, G6). Standardizes the incoming
/// <see cref="FeatureVector"/> with the model's training baseline, computes the softmax, and returns
/// the argmax protocol with its probability as the confidence.
///
/// Attribution evidence (P4, §7.3): the winning class's top-K contribution terms
/// (standardized feature value × class weight) are rendered as <see cref="EvidenceItem"/>s
/// (feature, human-readable raw value, contribution). This is the transparent stand-in for
/// SHAP/saliency on the production GBM/CNN — same evidence schema, so the UI renders it verbatim.
/// CPU-only and fast (&lt;200 ms, NFR-C4).
/// </summary>
public sealed class MlClassifier : IClassifier
{
    public const string ClassifierName = "ml-logreg-v1";

    /// <summary>Fixed seed/size for the built-in train-on-construction model (deterministic, P5).</summary>
    public const int DefaultTrainSeed = 20260707;
    public const int DefaultTrainSize = 3000;

    private readonly MlModel _model;
    private readonly int _topK;

    /// <summary>
    /// Builds a deterministic default model by training on a fixed synthetic seed when no
    /// <paramref name="model"/> artifact is supplied. Pass a loaded <see cref="MlModel"/> to run a
    /// checked-in / decode-self-labeled model instead.
    /// </summary>
    public MlClassifier(MlModel? model = null, int topK = 3)
    {
        if (topK < 1) throw new ArgumentOutOfRangeException(nameof(topK));
        _model = model ?? MlTrainer.Train(
            SyntheticDataset.Generate(DefaultTrainSeed, DefaultTrainSize), MlHyperparameters.Default);
        _topK = topK;
    }

    public SignalAtlas.Domain.Classification Classify(FeatureVector features)
    {
        int dim = _model.FeatureNames.Length;
        var raw = FeatureEncoder.Encode(features);
        var xStd = new double[dim];
        for (int j = 0; j < dim; j++) xStd[j] = (raw[j] - _model.Means[j]) / _model.Stds[j];

        var probs = new double[_model.Labels.Length];
        MlTrainer.Softmax(_model.Weights, _model.Bias, xStd, probs);

        int best = 0;
        for (int c = 1; c < probs.Length; c++)
            if (probs[c] > probs[best]) best = c;

        var evidence = TopContributions(best, xStd, features);
        return new SignalAtlas.Domain.Classification(
            _model.Labels[best], probs[best], ClassifierName, evidence);
    }

    /// <summary>Top-K signed contribution terms (feature value × class weight) toward the winner.</summary>
    private IReadOnlyList<EvidenceItem> TopContributions(int cls, double[] xStd, FeatureVector features)
    {
        int dim = _model.FeatureNames.Length;
        var wc = _model.Weights[cls];

        var order = Enumerable.Range(0, dim)
            .Select(j => (j, contrib: wc[j] * xStd[j]))
            .OrderByDescending(t => t.contrib) // most-positive push toward the winning class first
            .Take(Math.Min(_topK, dim))
            .ToList();

        var items = new List<EvidenceItem>(order.Count);
        foreach (var (j, contrib) in order)
            items.Add(new EvidenceItem(_model.FeatureNames[j], FeatureEncoder.DisplayValue(features, j), contrib));

        // Guaranteed non-empty (topK ≥ 1, dim ≥ 1) — AC-CL1 / P4.
        return items;
    }
}
