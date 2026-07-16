using SignalAtlas.Domain;

namespace SignalAtlas.Classification;

// NOTE: the assembly namespace ends in "Classification", so the unqualified name "Classification"
// resolves to THIS namespace (namespace lookup beats a using-alias). Refer to the Domain result
// type as the fully-qualified SignalAtlas.Domain.Classification throughout this project.

/// <summary>
/// Transparent weighted-rule protocol scorer (SPEC §8.3, G6). Each <see cref="ProtocolRule"/>
/// contributes weighted <see cref="EvidenceItem"/>s; the winning protocol's normalized score
/// (matched weight / total weight ∈ [0,1]) is the confidence. Below <see cref="ConfidenceFloor"/>
/// the result is <see cref="Classification.Unknown"/> — still with non-empty evidence explaining
/// why (AC-CL1, AC-CL3, P4). The ML model (M9) implements the same <see cref="IClassifier"/> contract.
/// </summary>
public sealed class RuleBasedClassifier : IClassifier
{
    public const string ClassifierName = "rule-based";

    /// <summary>Default minimum normalized score to accept a protocol (SPEC §8.3 AC-CL3).</summary>
    public const double DefaultConfidenceFloor = 0.5;

    private readonly IReadOnlyList<ProtocolRule> _rules;

    public RuleBasedClassifier(
        double confidenceFloor = DefaultConfidenceFloor,
        IReadOnlyList<ProtocolRule>? rules = null)
    {
        if (confidenceFloor is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidenceFloor), "Floor must be in [0,1].");
        ConfidenceFloor = confidenceFloor;
        _rules = rules ?? ReferenceRules.Default;
    }

    public double ConfidenceFloor { get; }

    public SignalAtlas.Domain.Classification Classify(FeatureVector features)
    {
        // Score every rule; the best normalized score (matched weight / total weight ∈ [0,1])
        // is the confidence for the winning protocol. Deterministic: ties resolve to rule order.
        ProtocolRule? best = null;
        double bestScore = 0.0;
        IReadOnlyList<EvidenceItem> bestEvidence = Array.Empty<EvidenceItem>();

        foreach (var rule in _rules)
        {
            var (score, evidence) = rule.Evaluate(features);
            if (best is null || score > bestScore)
            {
                best = rule;
                bestScore = score;
                bestEvidence = evidence;
            }
        }

        // Accept the winner only above the floor (AC-CL3); otherwise fall back to Unknown but
        // still explain the decision with non-empty evidence (AC-CL1, P4).
        if (best is not null && bestScore >= ConfidenceFloor && bestEvidence.Count > 0)
            return new SignalAtlas.Domain.Classification(best.Protocol, bestScore, ClassifierName, bestEvidence);

        return Unknown(best?.Protocol, bestScore, bestEvidence);
    }

    private SignalAtlas.Domain.Classification Unknown(
        string? bestProtocol, double bestScore, IReadOnlyList<EvidenceItem> partialEvidence)
    {
        var evidence = new List<EvidenceItem>(partialEvidence);
        var why = bestProtocol is null
            ? "no reference rule matched any feature"
            : $"best candidate '{bestProtocol}' scored {bestScore:0.###} < floor {ConfidenceFloor:0.###}";
        evidence.Add(new EvidenceItem("classification", why, bestScore));
        return new SignalAtlas.Domain.Classification(
            SignalAtlas.Domain.Classification.Unknown, bestScore, ClassifierName, evidence);
    }
}
