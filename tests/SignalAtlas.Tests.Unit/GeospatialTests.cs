using SignalAtlas.Domain;
using SignalAtlas.Geospatial;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M5 — Geospatial Engine (SPEC §8.6, G9). A single moving Rx cannot triangulate (§18.3), so the
/// engine never emits a false point fix: one obs → its position with a large floor uncertainty,
/// multiple → power-weighted centroid + honest uncertainty from the weighted spread. UncertaintyM
/// is ALWAYS > 0 (honest-limits invariant, §6.1 P7) and the core is deterministic (P5).
/// Small lat/lon deltas keep the equirectangular planar approximation valid.
/// </summary>
public class GeospatialTests
{
    private static readonly GeolocationEngine Engine = new();

    private static PositionedObservation Obs(double? lat, double? lon, double dbfs) =>
        new(lat, lon, dbfs);

    // 1. §8.6 — one obs → estimate equals that position; uncertainty ≥ single-sample floor (500 m).
    [Fact]
    public void SingleObservation_EstimateEqualsPosition_UncertaintyAtLeastFloor()
    {
        var est = Engine.Estimate(new[] { Obs(40.0, -73.0, -20.0) });

        Assert.NotNull(est);
        Assert.Equal(40.0, est!.Latitude, 9);
        Assert.Equal(-73.0, est.Longitude, 9);
        Assert.True(est.UncertaintyM >= 500.0);
    }

    // 2. §8.6 — symmetric equal-power cluster → centroid at the geometric center.
    [Fact]
    public void SymmetricEqualPowerCluster_CentroidAtGeometricCenter()
    {
        const double cLat = 10.0, cLon = 20.0, d = 0.01;
        var est = Engine.Estimate(new[]
        {
            Obs(cLat + d, cLon, -20.0),
            Obs(cLat - d, cLon, -20.0),
            Obs(cLat, cLon + d, -20.0),
            Obs(cLat, cLon - d, -20.0),
        });

        Assert.NotNull(est);
        Assert.Equal(cLat, est!.Latitude, 9);
        Assert.Equal(cLon, est.Longitude, 9);
        Assert.True(est.UncertaintyM > 0.0);
    }

    // 3. §8.6 — power weighting pulls the centroid toward the stronger observation vs equal weight.
    [Fact]
    public void PowerWeighting_PullsCentroidTowardStrongerObservation()
    {
        // Strong at lon 0, weak at lon 1.0; equal-weight midpoint would be 0.5.
        var est = Engine.Estimate(new[]
        {
            Obs(0.0, 0.0, -10.0),  // strong (linear power 0.1)
            Obs(0.0, 1.0, -30.0),  // weak   (linear power 0.001)
        });

        Assert.NotNull(est);
        Assert.Equal(0.0, est!.Latitude, 9);
        Assert.True(est.Longitude < 0.5, $"centroid lon {est.Longitude} should be pulled below 0.5");
        Assert.True(est.Longitude > 0.0);
    }

    // 4. §8.6 — no positioned obs → null (never a false 0,0 fix). Empty and all-null both apply.
    [Fact]
    public void NoPositionedObservations_ReturnsNull_NotZeroZero()
    {
        Assert.Null(Engine.Estimate(Array.Empty<PositionedObservation>()));
        Assert.Null(Engine.Estimate(new[]
        {
            Obs(null, null, -20.0),
            Obs(40.0, null, -20.0),   // partial position is not positioned
            Obs(null, -73.0, -20.0),
        }));
    }

    // 5. §8.6 — observations with null position are ignored; only positioned ones drive the estimate.
    [Fact]
    public void NullPositionObservations_AreIgnored()
    {
        var est = Engine.Estimate(new[]
        {
            Obs(null, null, -5.0),    // would dominate by power if not ignored
            Obs(40.0, -73.0, -20.0),
        });

        Assert.NotNull(est);
        Assert.Equal(40.0, est!.Latitude, 9);
        Assert.Equal(-73.0, est.Longitude, 9);
    }

    // 6. P7 — uncertainty is always > 0, even for coincident multi-observations (degenerate spread).
    [Fact]
    public void CoincidentObservations_UncertaintyStrictlyPositive()
    {
        var est = Engine.Estimate(new[]
        {
            Obs(1.0, 2.0, -15.0),
            Obs(1.0, 2.0, -15.0),
        });

        Assert.NotNull(est);
        Assert.True(est!.UncertaintyM > 0.0);
    }

    // 7. P5 — deterministic: same input yields an identical estimate.
    [Fact]
    public void Estimate_IsDeterministic()
    {
        var input = new[]
        {
            Obs(5.0, 5.0, -12.0),
            Obs(5.02, 5.03, -18.0),
            Obs(4.98, 4.97, -25.0),
        };

        var a = Engine.Estimate(input);
        var b = Engine.Estimate(input);

        Assert.Equal(a, b);
    }

    // 8. §8.6 — heatmap aggregation buckets positioned obs into grid cells with counts.
    [Fact]
    public void Heatmap_BucketsPositionedObservationsWithCounts()
    {
        var map = Engine.Heatmap(new[]
        {
            Obs(0.05, 0.05, -20.0),  // cell (0,0)
            Obs(0.07, 0.02, -20.0),  // cell (0,0)
            Obs(0.15, 0.05, -20.0),  // cell (1,0)
            Obs(null, null, -20.0),  // ignored
        }, cellDegrees: 0.1);

        Assert.Equal(2, map[new GridCell(0, 0)]);
        Assert.Equal(1, map[new GridCell(1, 0)]);
        Assert.False(map.ContainsKey(new GridCell(-1, -1)));
    }
}
