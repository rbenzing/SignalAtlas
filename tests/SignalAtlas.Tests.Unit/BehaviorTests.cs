using SignalAtlas.Behavior;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M6 — descriptive Behavior Engine (SPEC §8.7). Covers the §8.7 test list:
/// evenly-spaced → periodic w/ period; jitter-in-tolerance → still periodic;
/// continuous → always_on; sparse → burst; moving → mobile; clustered → stationary;
/// and P4 (evidence never empty) / P5 (deterministic) / no-throw edge cases.
/// </summary>
public class BehaviorTests
{
    private static readonly BehaviorEngine Engine = new();
    private static readonly DateTimeOffset T0 = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

    // A sighting at t0 + <seconds>, with an optional fixed position.
    private static Sighting At(double seconds, double? lat = null, double? lon = null)
        => new(T0.AddSeconds(seconds), lat, lon);

    private static IReadOnlyList<Sighting> Series(params double[] secs)
        => secs.Select(s => At(s)).ToList();

    // 1. Evenly-spaced arrivals → Periodic with PeriodS == mean gap.
    [Fact]
    public void EvenlySpaced_YieldsPeriodic_WithMeanPeriod()
    {
        var profile = Engine.Profile(Series(0, 60, 120, 180, 240, 300, 360));

        Assert.Equal(BehaviorProfile.Periodic, profile.Pattern);
        Assert.NotNull(profile.PeriodS);
        Assert.Equal(60.0, profile.PeriodS!.Value, 3);
        Assert.NotEmpty(profile.Evidence);
    }

    // 2. Small jitter within tolerance → still Periodic, period ≈ mean gap.
    [Fact]
    public void JitterWithinTolerance_StillPeriodic()
    {
        // Gaps ~60 s ±5 s (CoV well under threshold).
        var profile = Engine.Profile(Series(0, 58, 122, 179, 242, 299, 361));

        Assert.Equal(BehaviorProfile.Periodic, profile.Pattern);
        Assert.NotNull(profile.PeriodS);
        Assert.Equal(60.0, profile.PeriodS!.Value, 0); // ≈ 60 s mean
    }

    // 3a. Near-continuous, very small gaps relative to span → AlwaysOn (high duty).
    [Fact]
    public void ContinuousDenseArrivals_YieldsAlwaysOn()
    {
        var secs = Enumerable.Range(0, 201).Select(i => (double)i).ToArray(); // 1 s cadence, 200 s span
        var profile = Engine.Profile(Series(secs));

        Assert.Equal(BehaviorProfile.AlwaysOn, profile.Pattern);
        Assert.NotNull(profile.DutyCycle);
        Assert.True(profile.DutyCycle!.Value >= 0.9, $"duty {profile.DutyCycle} should be high");
    }

    // 3b. Sparse / irregular arrivals with long silences → Burst.
    [Fact]
    public void SparseIrregularArrivals_YieldsBurst()
    {
        // Two tight clusters separated by long silences.
        var profile = Engine.Profile(Series(0, 1, 2, 100, 101, 102, 300, 301));

        Assert.Equal(BehaviorProfile.Burst, profile.Pattern);
        Assert.NotEmpty(profile.Evidence);
    }

    // 4a. Positions that drift far apart → Mobile.
    [Fact]
    public void MovingPositions_YieldsMobile()
    {
        // ~0.001° latitude steps ≈ 111 m each → spread well beyond the threshold.
        var sightings = new List<Sighting>();
        for (var i = 0; i < 6; i++)
            sightings.Add(At(i * 60, lat: 40.0 + i * 0.001, lon: -75.0));

        var profile = Engine.Profile(sightings);

        Assert.Equal(BehaviorProfile.Mobile, profile.Mobility);
        Assert.NotEmpty(profile.Evidence);
    }

    // 4b. Tightly clustered positions → Stationary.
    [Fact]
    public void ClusteredPositions_YieldsStationary()
    {
        // ~0.00001° jitter ≈ 1 m → spread below the threshold.
        var sightings = new List<Sighting>();
        for (var i = 0; i < 6; i++)
            sightings.Add(At(i * 60, lat: 40.0 + i * 0.00001, lon: -75.0));

        var profile = Engine.Profile(sightings);

        Assert.Equal(BehaviorProfile.Stationary, profile.Mobility);
    }

    // No positions anywhere → default Stationary with evidence noting the absence.
    [Fact]
    public void NoPositions_DefaultsStationary_WithEvidence()
    {
        var profile = Engine.Profile(Series(0, 60, 120, 180));

        Assert.Equal(BehaviorProfile.Stationary, profile.Mobility);
        Assert.Contains(profile.Evidence, e => e.Feature.Contains("position"));
    }

    // P4: evidence is never empty, even for a single sighting.
    [Fact]
    public void Evidence_NeverEmpty_ForSingleSighting()
    {
        var profile = Engine.Profile(Series(0));
        Assert.NotEmpty(profile.Evidence);
    }

    // Edge cases must not throw and must return a sensible default with evidence.
    [Fact]
    public void EmptyAndSingle_DoNotThrow_AndAreInsufficientData()
    {
        var empty = Engine.Profile(Array.Empty<Sighting>());
        var single = Engine.Profile(Series(0));

        Assert.Equal(BehaviorProfile.Burst, empty.Pattern);
        Assert.Equal(BehaviorProfile.Burst, single.Pattern);
        Assert.NotEmpty(empty.Evidence);
        Assert.NotEmpty(single.Evidence);
    }

    // Unsorted input is normalized (P5 determinism): same result regardless of order.
    [Fact]
    public void UnsortedInput_IsClassifiedLikeSortedInput()
    {
        var sorted = Engine.Profile(Series(0, 60, 120, 180, 240, 300, 360));
        var shuffled = Engine.Profile(Series(180, 0, 300, 120, 360, 60, 240));

        Assert.Equal(sorted.Pattern, shuffled.Pattern);
        Assert.Equal(sorted.PeriodS, shuffled.PeriodS);
    }
}
