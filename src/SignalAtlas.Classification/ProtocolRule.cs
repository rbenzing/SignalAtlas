using SignalAtlas.Domain;

namespace SignalAtlas.Classification;

/// <summary>
/// One weighted feature test inside a <see cref="ProtocolRule"/>. When it matches it contributes
/// <see cref="Weight"/> to the rule's score and emits a cited <see cref="EvidenceItem"/> (P4).
/// </summary>
/// <param name="Feature">Feature-vector field this criterion inspects (e.g. "center_freq_hz").</param>
/// <param name="Expectation">Human-readable expectation, rendered verbatim by the UI (SPEC §7.3).</param>
/// <param name="Weight">Positive contribution to the rule score when matched.</param>
/// <param name="Evaluate">Returns whether the feature matches and the observed value to cite.</param>
public sealed record Criterion(
    string Feature,
    string Expectation,
    double Weight,
    Func<FeatureVector, (bool Matched, string Value)> Evaluate);

/// <summary>
/// A transparent, weighted rule for a single protocol. Score = matched weight / total weight,
/// so it is always ∈ [0,1] (AC-CL1). Every matched criterion is cited as evidence (SPEC §7.3, P4).
/// </summary>
public sealed class ProtocolRule
{
    public ProtocolRule(string protocol, params Criterion[] criteria)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            throw new ArgumentException("Protocol name required.", nameof(protocol));
        if (criteria is null || criteria.Length == 0)
            throw new ArgumentException("A rule needs at least one criterion.", nameof(criteria));

        Protocol = protocol;
        Criteria = criteria;
        TotalWeight = criteria.Sum(c => c.Weight);
        if (TotalWeight <= 0)
            throw new ArgumentException("Total criterion weight must be positive.", nameof(criteria));
    }

    public string Protocol { get; }
    public IReadOnlyList<Criterion> Criteria { get; }
    public double TotalWeight { get; }

    /// <summary>
    /// Scores the feature vector against this rule. Returns the normalized score ∈ [0,1] and
    /// the evidence for every criterion that matched (empty when nothing matched).
    /// </summary>
    public (double Score, IReadOnlyList<EvidenceItem> Evidence) Evaluate(FeatureVector features)
    {
        var evidence = new List<EvidenceItem>();
        double matched = 0.0;
        foreach (var c in Criteria)
        {
            var (ok, value) = c.Evaluate(features);
            if (!ok)
                continue;
            matched += c.Weight;
            evidence.Add(new EvidenceItem(c.Feature, $"{value} — {c.Expectation}", c.Weight));
        }

        return (matched / TotalWeight, evidence);
    }
}
