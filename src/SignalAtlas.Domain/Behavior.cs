namespace SignalAtlas.Domain;

/// <summary>
/// Descriptive behavior of an emitter (SPEC §7.2 behavior_profiles, §8.7).
/// Pattern ∈ {periodic, always_on, burst}; Mobility ∈ {stationary, mobile}. Evidence non-empty (P4).
/// </summary>
public sealed record BehaviorProfile(
    string Pattern,
    double? PeriodS,
    string Mobility,
    double? DutyCycle,
    IReadOnlyList<EvidenceItem> Evidence)
{
    public const string Periodic = "periodic";
    public const string AlwaysOn = "always_on";
    public const string Burst = "burst";
    public const string Stationary = "stationary";
    public const string Mobile = "mobile";
}

/// <summary>A single sighting: when it arrived and where (position optional).</summary>
public sealed record Sighting(DateTimeOffset Time, double? Latitude, double? Longitude);

/// <summary>
/// A per-emitter activity forecast (SPEC §8.11, §7.2 behavior_profiles.forecast). Predicts the
/// <paramref name="NextExpected"/> transmit time from the inter-arrival <paramref name="IntervalS"/>,
/// always carrying <paramref name="UncertaintyS"/> (never a false point estimate, §6.1 P4 honest-limits).
/// <paramref name="Method"/> names the estimator; Evidence is non-empty (P4).
/// </summary>
public sealed record Forecast(
    DateTimeOffset NextExpected,
    double IntervalS,
    double UncertaintyS,
    string Method,
    IReadOnlyList<EvidenceItem> Evidence);

/// <summary>
/// Predicts an emitter's next activity from its sighting history (SPEC §8.11): interval forecasting
/// with carried uncertainty. Deterministic (P5); returns null when history is too sparse to forecast.
/// </summary>
public interface IBehaviorPredictor
{
    Forecast? Predict(IReadOnlyList<Sighting> history);
}

/// <summary>
/// Derives a <see cref="BehaviorProfile"/> from an emitter's sightings (SPEC §8.7):
/// inter-arrival → periodic/always_on/burst; position variance → mobile/stationary.
/// </summary>
public interface IBehaviorEngine
{
    BehaviorProfile Profile(IReadOnlyList<Sighting> sightings);
}
