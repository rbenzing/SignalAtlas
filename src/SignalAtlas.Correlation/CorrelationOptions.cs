namespace SignalAtlas.Correlation;

/// <summary>
/// Tunable weights + threshold for the weighted RF-fallback scorer (SPEC §8.5, G8).
/// Defaults are documented per field. Weights need not sum to 1: the engine normalizes
/// over the components that actually have data on both sides (dynamic active-weight
/// normalization), so an emitter without a stored position simply drops the spatial term.
/// </summary>
public sealed record CorrelationOptions(
    // Frequency proximity dominates the no-ID decision — it is the only feature every
    // Emitter carries (FreqCenterHz + FreqStabilityHz).
    double FrequencyWeight = 0.50,
    // Bandwidth similarity: DORMANT — the shared Domain.Emitter contract has no bandwidth
    // column, so this weight is reserved but never contributes (see class remarks / report).
    double BandwidthWeight = 0.15,
    // Spatial proximity applies only when both input and emitter have positions.
    double SpatialWeight = 0.25,
    // Temporal proximity: DORMANT — Emitter carries no last-seen field to compare against.
    double TemporalWeight = 0.10,
    // Best normalized score must reach this to reuse an existing emitter (AC-CR3/CR4 boundary).
    double MatchThreshold = 0.60,
    // Two candidates whose scores differ by ≤ this are a tie → deterministic pick + evidence.
    double TieEpsilon = 1e-9,
    // Frequency tolerance = FreqStabilityHz × this. Within one stability window the score
    // stays high; it decays to 0 at (multiple × stability) of drift.
    double FrequencyToleranceMultiple = 3.0,
    // Default spatial tolerance (metres) when an emitter has no est_uncertainty_m.
    double SpatialToleranceMeters = 100.0)
{
    /// <summary>The documented default profile.</summary>
    public static CorrelationOptions Default { get; } = new();
}
