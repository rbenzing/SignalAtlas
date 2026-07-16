using System.Globalization;
using SignalAtlas.Domain;

namespace SignalAtlas.Behavior;

/// <summary>
/// Flags a <b>missed expected transmit</b> against a <see cref="Forecast"/> (SPEC §8.11): if the
/// injected current time has passed <c>NextExpected + k·UncertaintyS</c> and no new sighting arrived,
/// emit a <see cref="Alert.PredictedAnomaly"/>. The tolerance rides on the forecast's own uncertainty
/// (jitter), so a jittery emitter earns a wider grace window than a metronomic one (§6.1 P4).
/// <para>
/// <b>Tolerance k</b> = <see cref="ToleranceSigma"/> = 3 (a 3-σ window on the observed jitter). No wall
/// clock is read — <c>now</c> is injected (P5). Also exposes <see cref="EvaluateForecast"/> for
/// forecast-vs-actual error logging.
/// </para>
/// </summary>
public sealed class PredictedAnomalyDetector
{
    /// <summary>Tolerance multiplier k on the forecast uncertainty (σ) — a 3-σ grace window.</summary>
    public const double ToleranceSigma = 3.0;

    /// <summary>
    /// Returns a <see cref="Alert.PredictedAnomaly"/> when <paramref name="now"/> is past
    /// <c>NextExpected + k·UncertaintyS</c> with no transmission since the forecast's basis, else null.
    /// </summary>
    /// <param name="lastSightingTime">Time of the most recent sighting observed, if any. A sighting
    /// after the forecast basis (NextExpected − IntervalS) means the emitter was heard → not missed.</param>
    public Alert? Evaluate(
        string? emitterId,
        string? deviceId,
        Forecast forecast,
        DateTimeOffset now,
        DateTimeOffset? lastSightingTime)
    {
        var toleranceS = ToleranceSigma * forecast.UncertaintyS;
        var deadline = forecast.NextExpected.AddSeconds(toleranceS);
        var basis = forecast.NextExpected.AddSeconds(-forecast.IntervalS);

        // Heard since the forecast was made → the emitter transmitted; nothing missed.
        if (lastSightingTime.HasValue && lastSightingTime.Value > basis)
            return null;

        // Still inside the grace window → not (yet) a missed transmit.
        if (now <= deadline)
            return null;

        var gapS = (now - forecast.NextExpected).TotalSeconds;
        return new Alert(
            Id: DeterministicGuid.From($"{Alert.PredictedAnomaly}:{emitterId}:{forecast.NextExpected:o}"),
            Time: now,
            EmitterId: emitterId,
            DeviceId: deviceId,
            Kind: Alert.PredictedAnomaly,
            Severity: "warning",
            Summary: $"Missed expected transmit: {Num(gapS)} s past forecast (tolerance {Num(toleranceS)} s).",
            Evidence: new List<EvidenceItem>
            {
                new("next_expected", forecast.NextExpected.ToString("o", CultureInfo.InvariantCulture), 0.0),
                new("now", now.ToString("o", CultureInfo.InvariantCulture), 0.0),
                new("gap_s", Num(gapS), gapS),
                new("tolerance_s", Num(toleranceS), toleranceS),
                new("interval_s", Num(forecast.IntervalS), forecast.IntervalS),
                new("uncertainty_s", Num(forecast.UncertaintyS), forecast.UncertaintyS),
            });
    }

    /// <summary>
    /// Forecast-vs-actual error in seconds: <c>actualNext − NextExpected</c> (signed; positive = late).
    /// ~0 for an on-time actual, growing as the actual drifts from the forecast — for logging (§8.11).
    /// </summary>
    public static double EvaluateForecast(Forecast forecast, DateTimeOffset actualNext)
        => (actualNext - forecast.NextExpected).TotalSeconds;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
