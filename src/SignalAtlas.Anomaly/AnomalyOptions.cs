namespace SignalAtlas.Anomaly;

/// <summary>
/// Tunable, documented thresholds for the rule-based <see cref="AnomalyEngine"/> (SPEC §8.8).
/// All thresholds are <b>relative</b> (§4.5 / G20): power is compared in dBFS deltas, never
/// absolute dBm. Defaults are conservative so that ordinary drift/jitter does not alert.
/// </summary>
/// <param name="LocationChangeThresholdMeters">
/// Great-circle move (metres) from the baseline position above which a location_change fires.
/// Default 100 m — larger than the 50 m mobility spread (§8.7) so a stationary emitter's
/// GPS jitter is not flagged as a relocation.
/// </param>
/// <param name="PowerChangeThresholdDb">
/// Absolute dBFS delta from the baseline above which a power_change fires. Default 6 dB
/// (≈ a 4× linear change) — comfortably above normal fading, so only a real level shift alerts.
/// </param>
/// <param name="OccupancySpikeFactor">
/// Multiplicative factor over baseline occupancy above which an occupancy_spike fires.
/// Default 2.0 — the event's occupancy must exceed twice the baseline to count as a spike.
/// </param>
public sealed record AnomalyOptions(
    double LocationChangeThresholdMeters = 100.0,
    double PowerChangeThresholdDb = 6.0,
    double OccupancySpikeFactor = 2.0)
{
    /// <summary>The documented default thresholds.</summary>
    public static AnomalyOptions Default { get; } = new();
}
