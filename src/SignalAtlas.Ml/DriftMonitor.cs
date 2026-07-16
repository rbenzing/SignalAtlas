using SignalAtlas.Domain;

namespace SignalAtlas.Ml;

/// <summary>
/// Feature-distribution drift monitor (SPEC §8.9). Compares the per-feature MEAN of a recent stream
/// against the training baseline (<see cref="MlModel.Means"/>/<see cref="MlModel.Stds"/>) captured
/// at model-build time. For each feature it computes the standardized shift
/// |mean(recent) − mean_train| / std_train; the stream is flagged as drifting when ANY numeric
/// feature's shift exceeds <see cref="Threshold"/> (default 2.0 σ — a two-sigma move of the batch
/// mean is well outside sampling noise). Firing signals that NFR-A2 guarantees may no longer hold
/// and a retrain on fresh decode-self-labeled data (§12.2) is warranted.
/// </summary>
public sealed class DriftMonitor : IDriftMonitor
{
    /// <summary>Documented default: a 2σ shift of a feature's batch mean vs. the training baseline.</summary>
    public const double DefaultThreshold = 2.0;

    // Only the continuous numeric features are monitored (the one-hot modulation columns are not
    // meaningful as "means" for a drift z-score); indices 0..6 per FeatureEncoder.
    private const int NumericFeatureCount = 7;

    private readonly MlModel _baseline;

    public DriftMonitor(MlModel baseline, double threshold = DefaultThreshold)
    {
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        _baseline = baseline;
        Threshold = threshold;
    }

    public double Threshold { get; }

    public bool IsDrifting(IReadOnlyList<FeatureVector> recentFeatures)
    {
        if (recentFeatures.Count == 0) return false;

        var sums = new double[NumericFeatureCount];
        foreach (var f in recentFeatures)
        {
            var row = FeatureEncoder.Encode(f);
            for (int j = 0; j < NumericFeatureCount; j++) sums[j] += row[j];
        }

        for (int j = 0; j < NumericFeatureCount; j++)
        {
            double recentMean = sums[j] / recentFeatures.Count;
            double shift = Math.Abs(recentMean - _baseline.Means[j]) / _baseline.Stds[j];
            if (shift > Threshold) return true;
        }

        return false;
    }
}
