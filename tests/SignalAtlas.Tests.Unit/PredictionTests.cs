using SignalAtlas.Behavior;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M11 — Behavior Prediction (SPEC §8.11, §6.1 P4/P5). Covers the §8.11 test list:
/// periodic next-event within tolerance; forecast carries jitter-derived uncertainty;
/// missed expected transmit → predicted_anomaly (and NOT when heard on time);
/// forecast-vs-actual error (~0 on time, grows late); deterministic forecasts.
/// </summary>
public class PredictionTests
{
    private static readonly BehaviorPredictor Predictor = new();
    private static readonly PredictedAnomalyDetector Detector = new();
    private static readonly DateTimeOffset T0 = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

    private static Sighting At(double seconds) => new(T0.AddSeconds(seconds), null, null);

    private static IReadOnlyList<Sighting> Series(params double[] secs)
        => secs.Select(At).ToList();

    // 1. Evenly-spaced arrivals → next expected ≈ last + mean interval.
    [Fact]
    public void EvenlySpaced_PredictsNextEvent_WithinTolerance()
    {
        var forecast = Predictor.Predict(Series(0, 60, 120, 180, 240, 300, 360));

        Assert.NotNull(forecast);
        Assert.Equal(BehaviorPredictor.MeanInterval, forecast!.Method);
        Assert.Equal(60.0, forecast.IntervalS, 3);
        var expected = T0.AddSeconds(420);
        Assert.True(Math.Abs((forecast.NextExpected - expected).TotalSeconds) < 1.0,
            $"NextExpected {forecast.NextExpected:o} should be ≈ {expected:o}");
        Assert.NotEmpty(forecast.Evidence);
    }

    // 2. Forecast carries non-zero uncertainty derived from jitter; more jitter → larger uncertainty.
    [Fact]
    public void Forecast_CarriesUncertainty_GrowingWithJitter()
    {
        // Low jitter: gaps ~60 ±2 s.
        var low = Predictor.Predict(Series(0, 62, 118, 182, 238, 302, 358));
        // High jitter: gaps ~60 ±20 s.
        var high = Predictor.Predict(Series(0, 80, 120, 200, 240, 320, 360));

        Assert.NotNull(low);
        Assert.NotNull(high);
        Assert.True(low!.UncertaintyS > 0.0, "jittered history must carry non-zero uncertainty");
        Assert.True(high!.UncertaintyS > low.UncertaintyS,
            $"more jitter ({high.UncertaintyS}) should exceed less ({low.UncertaintyS})");
    }

    // 3a. Current time well past NextExpected + tolerance with no new sighting → predicted_anomaly.
    [Fact]
    public void MissedTransmit_EmitsPredictedAnomaly_WithEvidence()
    {
        var forecast = Predictor.Predict(Series(0, 80, 120, 200, 240, 320, 360));
        Assert.NotNull(forecast);

        // now = well past deadline (next expected + k·uncertainty); last sighting is the one at 360 s.
        var deadline = forecast!.NextExpected.AddSeconds(
            PredictedAnomalyDetector.ToleranceSigma * forecast.UncertaintyS);
        var now = deadline.AddSeconds(10 * forecast.IntervalS);

        var alert = Detector.Evaluate("emitter-1", null, forecast, now, T0.AddSeconds(360));

        Assert.NotNull(alert);
        Assert.Equal(Alert.PredictedAnomaly, alert!.Kind);
        Assert.Equal("emitter-1", alert.EmitterId);
        Assert.NotEmpty(alert.Evidence);
    }

    // 3b. A sighting arrived on time (within tolerance) → no anomaly.
    [Fact]
    public void OnTimeSighting_NoAnomaly()
    {
        var forecast = Predictor.Predict(Series(0, 80, 120, 200, 240, 320, 360));
        Assert.NotNull(forecast);

        // Emitter was heard right at the expected time; now is just after.
        var heardAt = forecast!.NextExpected;
        var now = forecast.NextExpected.AddSeconds(1);

        var alert = Detector.Evaluate("emitter-1", null, forecast, now, heardAt);

        Assert.Null(alert);
    }

    // 3c. Still inside the tolerance window, no sighting yet → not (yet) an anomaly.
    [Fact]
    public void WithinToleranceWindow_NoAnomaly()
    {
        var forecast = Predictor.Predict(Series(0, 80, 120, 200, 240, 320, 360));
        Assert.NotNull(forecast);

        var now = forecast!.NextExpected.AddSeconds(0.5 * forecast.UncertaintyS); // < k·σ
        var alert = Detector.Evaluate("emitter-1", null, forecast, now, T0.AddSeconds(360));

        Assert.Null(alert);
    }

    // 4. forecast-vs-actual: ~0 for an on-time actual, grows for a late one.
    [Fact]
    public void EvaluateForecast_ZeroOnTime_GrowsWhenLate()
    {
        var forecast = Predictor.Predict(Series(0, 60, 120, 180, 240, 300, 360));
        Assert.NotNull(forecast);

        var onTime = PredictedAnomalyDetector.EvaluateForecast(forecast!, forecast!.NextExpected);
        var late = PredictedAnomalyDetector.EvaluateForecast(
            forecast, forecast.NextExpected.AddSeconds(120));

        Assert.True(Math.Abs(onTime) < 1e-6, $"on-time error {onTime} should be ≈ 0");
        Assert.True(Math.Abs(late) > Math.Abs(onTime), "a late actual must grow the error");
        Assert.Equal(120.0, late, 6); // signed seconds, positive = late
    }

    // 5. Determinism (P5): identical history → identical forecast.
    [Fact]
    public void Deterministic_SameHistory_IdenticalForecast()
    {
        var a = Predictor.Predict(Series(0, 80, 120, 200, 240, 320, 360));
        var b = Predictor.Predict(Series(0, 80, 120, 200, 240, 320, 360));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.NextExpected, b!.NextExpected);
        Assert.Equal(a.IntervalS, b.IntervalS);
        Assert.Equal(a.UncertaintyS, b.UncertaintyS);
        Assert.Equal(a.Method, b.Method);
        Assert.True(a.Evidence.SequenceEqual(b.Evidence), "evidence must be identical (P5)");
    }

    // Insufficient data (<2 sightings) → null; exactly 2 → honest low-confidence forecast.
    [Fact]
    public void InsufficientData_NullOrLowConfidence()
    {
        Assert.Null(Predictor.Predict(Series()));
        Assert.Null(Predictor.Predict(Series(0)));

        var two = Predictor.Predict(Series(0, 60));
        Assert.NotNull(two);
        Assert.Equal(BehaviorPredictor.InsufficientData, two!.Method);
        Assert.True(two.UncertaintyS >= two.IntervalS,
            "with a single interval, uncertainty must be large (no jitter estimate)");
    }

    // Nightly: rolling forecast-vs-actual over a long jittered series stays bounded by uncertainty.
    [Fact]
    [Trait("Category", "Nightly")]
    public void ForecastVsActual_RollingError_BoundedByUncertainty()
    {
        var rng = new Random(1234); // seeded → deterministic (P5)
        var times = new List<double>();
        double t = 0;
        for (var i = 0; i < 500; i++)
        {
            times.Add(t);
            t += 60 + (rng.NextDouble() - 0.5) * 20; // 60 s ±10 s jitter
        }

        var errors = new List<double>();
        for (var i = 30; i < times.Count - 1; i++)
        {
            var history = times.Take(i).Select(At).ToList();
            var forecast = Predictor.Predict(history);
            Assert.NotNull(forecast);
            var actualNext = T0.AddSeconds(times[i]);
            errors.Add(Math.Abs(PredictedAnomalyDetector.EvaluateForecast(forecast!, actualNext)));
        }

        var meanAbsError = errors.Average();
        var finalForecast = Predictor.Predict(times.Take(times.Count - 1).Select(At).ToList())!;
        Assert.True(meanAbsError < PredictedAnomalyDetector.ToleranceSigma * finalForecast.UncertaintyS,
            $"mean abs error {meanAbsError} should sit within k·σ {PredictedAnomalyDetector.ToleranceSigma * finalForecast.UncertaintyS}");
    }
}
