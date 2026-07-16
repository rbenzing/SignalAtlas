using SignalAtlas.Domain;

namespace SignalAtlas.Ml;

/// <summary>A <see cref="FeatureVector"/> paired with its ground-truth protocol label (SPEC §12.2).</summary>
public sealed record LabeledSample(FeatureVector Features, string Label);
