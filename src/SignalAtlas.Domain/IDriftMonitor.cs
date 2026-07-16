namespace SignalAtlas.Domain;

/// <summary>
/// Feature-distribution drift monitor (SPEC §8.9). Compares the statistics of a recent
/// stream of <see cref="FeatureVector"/>s against the training baseline captured at model
/// build time. Returns true when the stream has shifted beyond a documented threshold,
/// signalling that the classifier's accuracy guarantees (NFR-A2) may no longer hold and a
/// retrain on fresh decode-self-labeled data (§12.2) is warranted.
/// </summary>
public interface IDriftMonitor
{
    /// <summary>True when <paramref name="recentFeatures"/> is out-of-distribution vs. baseline.</summary>
    bool IsDrifting(IReadOnlyList<FeatureVector> recentFeatures);
}
