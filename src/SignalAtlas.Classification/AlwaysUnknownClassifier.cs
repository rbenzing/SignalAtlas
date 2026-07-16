using SignalAtlas.Domain;

namespace SignalAtlas.Classification;

// NOTE: the assembly namespace ends in "Classification", so the unqualified name "Classification"
// resolves to THIS namespace (namespace lookup beats a using-alias). Refer to the Domain result
// type as the fully-qualified SignalAtlas.Domain.Classification throughout this project.

/// <summary>
/// Null-object <see cref="IClassifier"/> that always returns <see cref="Classification.Unknown"/>
/// with a single explanatory evidence item (SPEC §8.3 test list: "stub IClassifier keeps
/// pipeline green"). Lets the pipeline stay green before the rule set matures, while still
/// honouring the never-empty-evidence invariant (AC-CL1) and P4 (explainable only).
/// </summary>
public sealed class AlwaysUnknownClassifier : IClassifier
{
    public const string ClassifierName = "always-unknown";

    public SignalAtlas.Domain.Classification Classify(FeatureVector features) =>
        new(SignalAtlas.Domain.Classification.Unknown, 0.0, ClassifierName,
            new[] { new EvidenceItem("classifier", "stub: no rules evaluated", 0.0) });
}
