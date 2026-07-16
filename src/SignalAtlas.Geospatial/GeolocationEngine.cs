using SignalAtlas.Domain;

namespace SignalAtlas.Geospatial;

/// <summary>A heatmap grid cell index (SPEC §8.6 heatmap aggregation).</summary>
public readonly record struct GridCell(int LatIndex, int LonIndex);

/// <summary>
/// Honest geospatial estimator (SPEC §8.6, G9). A single moving receiver cannot triangulate
/// (§18.3: multilateration needs ≥3 synced sensors / TDOA), so this never emits a false point fix:
/// one obs → its position with a large single-sample floor; multiple → power-weighted centroid with
/// uncertainty derived from the weighted spread. <see cref="LocationEstimate.UncertaintyM"/> is
/// ALWAYS &gt; 0 (honest-limits invariant, §6.1 P7). Deterministic (P5).
///
/// Positions use an equirectangular planar approximation to convert lat/lon deltas to metres, valid
/// for the small deltas expected within a single receiver's coverage footprint (§8.6).
/// </summary>
public sealed class GeolocationEngine : IGeolocationEngine
{
    /// <summary>Single-sample uncertainty floor: one obs can localise no better than this (§18.3).</summary>
    public const double SingleObservationFloorM = 500.0;

    /// <summary>Strictly-positive uncertainty floor for the multi-obs case (P7): never report 0 m.</summary>
    public const double MinUncertaintyM = 1.0;

    private const double MetersPerDegreeLat = 111_320.0;

    public LocationEstimate? Estimate(IReadOnlyList<PositionedObservation> observations)
    {
        // Use only fully-positioned observations; ignore null / partial positions (§8.6).
        var positioned = new List<(double Lat, double Lon, double Weight)>(observations.Count);
        foreach (var o in observations)
        {
            if (o.Latitude is not double lat || o.Longitude is not double lon)
                continue;
            // Linear power weight from dBFS: w = 10^(dBFS/10). Stronger obs pull the centroid.
            positioned.Add((lat, lon, Math.Pow(10.0, o.PowerDbfs / 10.0)));
        }

        // No positioned obs → null, never a false (0,0) fix (§8.6).
        if (positioned.Count == 0)
            return null;

        double sumW = 0.0, sumWLat = 0.0, sumWLon = 0.0;
        foreach (var (lat, lon, w) in positioned)
        {
            sumW += w;
            sumWLat += w * lat;
            sumWLon += w * lon;
        }

        var centLat = sumWLat / sumW;
        var centLon = sumWLon / sumW;

        // One positioned obs → its position with a large single-sample floor (§18.3).
        if (positioned.Count == 1)
            return new LocationEstimate(centLat, centLon, SingleObservationFloorM);

        // Multiple → uncertainty = weighted RMS distance to the centroid, floored strictly positive.
        var metersPerDegLon = MetersPerDegreeLat * Math.Cos(centLat * Math.PI / 180.0);
        double sumWSq = 0.0;
        foreach (var (lat, lon, w) in positioned)
        {
            var dy = (lat - centLat) * MetersPerDegreeLat;
            var dx = (lon - centLon) * metersPerDegLon;
            sumWSq += w * (dx * dx + dy * dy);
        }

        var weightedRmsM = Math.Sqrt(sumWSq / sumW);
        var uncertaintyM = Math.Max(weightedRmsM, MinUncertaintyM);

        return new LocationEstimate(centLat, centLon, uncertaintyM);
    }

    /// <summary>
    /// Buckets positioned observations into a regular lat/lon grid (SPEC §8.6 heatmap aggregation),
    /// keyed by floored cell index. Null / partial positions are ignored.
    /// </summary>
    public IReadOnlyDictionary<GridCell, int> Heatmap(
        IReadOnlyList<PositionedObservation> observations, double cellDegrees)
    {
        if (cellDegrees <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(cellDegrees), "cellDegrees must be > 0.");

        var cells = new Dictionary<GridCell, int>();
        foreach (var o in observations)
        {
            if (o.Latitude is not double lat || o.Longitude is not double lon)
                continue;

            var cell = new GridCell(
                (int)Math.Floor(lat / cellDegrees),
                (int)Math.Floor(lon / cellDegrees));
            cells[cell] = cells.TryGetValue(cell, out var c) ? c + 1 : 1;
        }

        return cells;
    }
}
