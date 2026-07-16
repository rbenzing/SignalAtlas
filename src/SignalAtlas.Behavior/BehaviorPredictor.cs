using System.Globalization;
using SignalAtlas.Domain;

namespace SignalAtlas.Behavior;

/// <summary>
/// Per-emitter interval forecaster (SPEC §8.11, §6.1 P4/P5). Forecasts the next expected transmit
/// time purely from inter-arrival timing:
/// <list type="bullet">
///   <item><b>NextExpected</b> = last sighting + mean inter-arrival interval.</item>
///   <item><b>UncertaintyS</b> = the (population) standard deviation of the observed intervals — the
///   jitter. It is <i>always</i> carried alongside the point estimate so downstream code never treats
///   the forecast as exact (§6.1 P4 honest-limits): more jitter → larger uncertainty.</item>
///   <item><b>Method</b> = <see cref="MeanInterval"/> with ≥ <see cref="MinSightings"/> sightings
///   (≥2 intervals, so jitter is estimable); <see cref="InsufficientData"/> for exactly 2 sightings
///   (a single interval → jitter unknown, uncertainty floored to the whole interval); <c>null</c> for
///   &lt; 2 sightings (no interval at all).</item>
/// </list>
/// Deterministic (P5): the history is time-sorted first and no wall clock / RNG is consulted.
/// Evidence is always non-empty (P4), citing interval statistics.
/// </summary>
public sealed class BehaviorPredictor : IBehaviorPredictor
{
    /// <summary>Estimator name when jitter is measurable (≥2 intervals).</summary>
    public const string MeanInterval = "mean-interval";

    /// <summary>Estimator name when only a single interval exists (jitter not estimable).</summary>
    public const string InsufficientData = "insufficient-data";

    /// <summary>Minimum sightings for a full mean-interval forecast (≥2 intervals for a jitter estimate).</summary>
    public const int MinSightings = 3;

    public Forecast? Predict(IReadOnlyList<Sighting> history)
    {
        var ordered = history.OrderBy(s => s.Time).ToList();

        // Fewer than 2 sightings → no interval at all → cannot forecast (return null, never a guess).
        if (ordered.Count < 2)
            return null;

        var gaps = new double[ordered.Count - 1];
        for (var i = 1; i < ordered.Count; i++)
            gaps[i - 1] = (ordered[i].Time - ordered[i - 1].Time).TotalSeconds;

        var mean = gaps.Average();
        var last = ordered[^1].Time;
        var nextExpected = last.AddSeconds(mean);

        // A single interval: jitter is unknowable, so carry a large uncertainty (the whole interval)
        // and flag the method as insufficient-data rather than pretend to a precise σ.
        if (ordered.Count < MinSightings)
        {
            return new Forecast(
                nextExpected,
                mean,
                Math.Abs(mean),
                InsufficientData,
                new List<EvidenceItem>
                {
                    new("sighting_count", ordered.Count.ToString(CultureInfo.InvariantCulture), ordered.Count),
                    new("interval_s", Num(mean), mean),
                    new("method", "single interval — uncertainty = full interval (jitter unknown)", 0.0),
                });
        }

        var variance = gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Length;
        var stdDev = Math.Sqrt(variance);

        var evidence = new List<EvidenceItem>
        {
            new("sighting_count", ordered.Count.ToString(CultureInfo.InvariantCulture), ordered.Count),
            new("interval_count", gaps.Length.ToString(CultureInfo.InvariantCulture), gaps.Length),
            new("mean_interval_s", Num(mean), mean),
            new("interval_stddev_s", Num(stdDev), stdDev),
            new("last_seen", last.ToString("o", CultureInfo.InvariantCulture), 0.0),
            new("method", MeanInterval, mean),
        };

        return new Forecast(nextExpected, mean, stdDev, MeanInterval, evidence);
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
