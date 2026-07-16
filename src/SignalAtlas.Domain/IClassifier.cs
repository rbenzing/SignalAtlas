namespace SignalAtlas.Domain;

/// <summary>The result of classifying a <see cref="FeatureVector"/> (SPEC §8.3).</summary>
public sealed record Classification(
    string Protocol,
    double Confidence,
    string Classifier,
    IReadOnlyList<EvidenceItem> Evidence)
{
    /// <summary>The reserved protocol name for a below-floor / no-match result (SPEC §8.3 AC-CL3).</summary>
    public const string Unknown = "Unknown";
}

/// <summary>
/// Feature vector → protocol + confidence + evidence, or Unknown (SPEC §8.3, G6).
/// The rule scorer (M2) and the ML model (M9) both implement this same contract.
/// Invariant: Evidence is never empty and Confidence ∈ [0,1] (AC-CL1).
/// </summary>
public interface IClassifier
{
    Classification Classify(FeatureVector features);
}
