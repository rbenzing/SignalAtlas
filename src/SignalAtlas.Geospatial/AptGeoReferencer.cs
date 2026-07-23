namespace SignalAtlas.Geospatial;

/// <summary>
/// The approximate NOAA APT image-overlay quadrilateral (SPEC §8.4 Phase 2 design §1/§3): 4
/// lon/lat corner pairs ordered TL, TR, BR, BL, ready to hand to a MapLibre <c>image</c> source.
/// <see cref="Approximate"/> is always <c>true</c> for this rectangular-swath georef (design §1
/// "honesty about scope") — there is no non-approximate mode.
/// </summary>
public sealed record AptGeoQuad(double[][] Corners, bool Approximate);

/// <summary>
/// Computes the approximate rectangular georeference quad for a decoded NOAA APT pass (SPEC §8.4
/// Phase 2 design §3): propagates the satellite's sub-point at the start and end of the pass, then
/// offsets each along-track point perpendicular to the ground track by the nominal APT half-swath
/// (design §3, <see cref="SwathHalfWidthKm"/> ≈ 1450 km) to bracket the image extent.
///
/// <para>Deterministic (P5): a pure function of (satellite, passStart, lines, tle) — no clock, no
/// randomness. Not registered as anything but stateless (safe to share as a singleton).</para>
/// </summary>
public sealed class AptGeoReferencer
{
    /// <summary>Nominal APT swath half-width either side of the ground track (design §3).</summary>
    private const double SwathHalfWidthKm = 1450.0;

    /// <summary>Mean Earth radius (km) used for the spherical destination-point formula (design §3).</summary>
    private const double EarthRadiusKm = 6371.0;

    /// <summary>APT transmits 2 image lines/second (design §3): lines -> pass duration in seconds.</summary>
    private const double LinesPerSecond = 2.0;

    /// <summary>
    /// Returns the approximate overlay quad for a NOAA APT pass, or <c>null</c> when the input is
    /// unusable (non-positive line count, or the propagator can't handle this TLE — e.g. a
    /// deep-space TLE outside the near-Earth SGP4 scope, SPEC §8.4 Phase 2 §3) — offline-degrading
    /// (design §1): the caller turns a null into a 404, never a throw.
    /// </summary>
    public AptGeoQuad? Reference(string satellite, DateTimeOffset passStart, int lines, Tle tle)
    {
        ArgumentNullException.ThrowIfNull(satellite);
        ArgumentNullException.ThrowIfNull(tle);

        if (lines <= 0)
            return null;

        var passEnd = passStart + TimeSpan.FromSeconds(lines / LinesPerSecond);

        GeoPoint subStart;
        GeoPoint subEnd;
        try
        {
            subStart = Sgp4.Propagate(tle, passStart);
            subEnd = Sgp4.Propagate(tle, passEnd);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            // Deep-space TLE (out of SGP4 scope) or a numerically invalid propagation — unusable.
            return null;
        }

        if (subStart.LatDeg == subEnd.LatDeg && subStart.LonDeg == subEnd.LonDeg)
            return null; // degenerate: no track, so no well-defined heading/perpendicular.

        var headingDeg = InitialBearingDeg(subStart, subEnd);
        var perpLeftDeg = NormalizeDeg(headingDeg - 90.0);
        var perpRightDeg = NormalizeDeg(headingDeg + 90.0);

        var tl = Destination(subStart, SwathHalfWidthKm, perpLeftDeg);
        var tr = Destination(subStart, SwathHalfWidthKm, perpRightDeg);
        var br = Destination(subEnd, SwathHalfWidthKm, perpRightDeg);
        var bl = Destination(subEnd, SwathHalfWidthKm, perpLeftDeg);

        var corners = new[]
        {
            new[] { tl.LonDeg, tl.LatDeg },
            new[] { tr.LonDeg, tr.LatDeg },
            new[] { br.LonDeg, br.LatDeg },
            new[] { bl.LonDeg, bl.LatDeg },
        };

        return new AptGeoQuad(corners, Approximate: true);
    }

    /// <summary>Initial great-circle bearing (degrees, 0-360) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static double InitialBearingDeg(GeoPoint from, GeoPoint to)
    {
        var lat1 = DegToRad(from.LatDeg);
        var lat2 = DegToRad(to.LatDeg);
        var deltaLon = DegToRad(to.LonDeg - from.LonDeg);

        var y = Math.Sin(deltaLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon);
        var bearingRad = Math.Atan2(y, x);
        return NormalizeDeg(RadToDeg(bearingRad));
    }

    /// <summary>
    /// Standard spherical destination-point formula (Vincenty-style great-circle offset): the point
    /// <paramref name="distanceKm"/> away from <paramref name="from"/> along initial bearing
    /// <paramref name="bearingDeg"/>, on a sphere of radius <see cref="EarthRadiusKm"/>.
    /// </summary>
    private static GeoPoint Destination(GeoPoint from, double distanceKm, double bearingDeg)
    {
        var angularDistance = distanceKm / EarthRadiusKm;
        var bearingRad = DegToRad(bearingDeg);
        var lat1 = DegToRad(from.LatDeg);
        var lon1 = DegToRad(from.LonDeg);

        var lat2 = Math.Asin(
            Math.Sin(lat1) * Math.Cos(angularDistance) +
            Math.Cos(lat1) * Math.Sin(angularDistance) * Math.Cos(bearingRad));

        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearingRad) * Math.Sin(angularDistance) * Math.Cos(lat1),
            Math.Cos(angularDistance) - Math.Sin(lat1) * Math.Sin(lat2));

        return new GeoPoint(RadToDeg(lat2), NormalizeLonDeg(RadToDeg(lon2)), from.AltKm);
    }

    private static double NormalizeDeg(double deg)
    {
        var wrapped = deg % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }

    private static double NormalizeLonDeg(double deg)
    {
        var wrapped = deg;
        while (wrapped > 180.0) wrapped -= 360.0;
        while (wrapped < -180.0) wrapped += 360.0;
        return wrapped;
    }

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;

    private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;
}
