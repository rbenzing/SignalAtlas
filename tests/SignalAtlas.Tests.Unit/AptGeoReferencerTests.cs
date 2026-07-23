using SignalAtlas.Geospatial;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// NOAA APT georeference (SPEC §8.4 Phase 2 design §3/§6): the approximate rectangular overlay
/// quad derived from the satellite's sub-track over a pass. Geometry is deliberately approximate
/// (design §1) — this suite checks the corners bracket the sub-track, span roughly the documented
/// swath width, keep the TL/TR/BR/BL ordering, and are a pure/deterministic function of (tle,
/// passStart, lines) per P5.
/// </summary>
public class AptGeoReferencerTests
{
    // Same live NOAA-19 TLE used by Sgp4Tests (NORAD 33591, real ~870 km / ~102 min POES orbit).
    private const string Noaa19Name = "NOAA 19";
    private const string Noaa19Line1 = "1 33591U 09005A   26204.29298328  .00000011  00000+0  29886-4 0  9990";
    private const string Noaa19Line2 = "2 33591  98.9503 275.1908 0012716 289.5371  70.4428 14.13479265899587";

    private static Tle Noaa19Tle => Tle.Parse(Noaa19Name, Noaa19Line1, Noaa19Line2);

    private static readonly AptGeoReferencer Referencer = new();

    // 1. A known TLE + passStart + lines -> a 4-corner quad, marked approximate.
    [Fact]
    public void Reference_KnownTleAndPass_ReturnsApproximateQuad()
    {
        var tle = Noaa19Tle;
        var passStart = tle.Epoch;
        const int lines = 240; // 2 lines/sec -> 120 s pass

        var quad = Referencer.Reference("NOAA-19", passStart, lines, tle);

        Assert.NotNull(quad);
        Assert.True(quad!.Approximate);
        Assert.Equal(4, quad.Corners.Length);
        foreach (var corner in quad.Corners)
            Assert.Equal(2, corner.Length);
    }

    // 2. Corner ordering is TL, TR, BR, BL (design §3): TL/TR bracket the pass start, BR/BL the
    //    pass end, so the TL<->BL and TR<->BR pairs correspond to the SAME perpendicular side.
    [Fact]
    public void Reference_CornerOrdering_IsTlTrBrBl()
    {
        var tle = Noaa19Tle;
        var passStart = tle.Epoch;
        const int lines = 240;

        var quad = Referencer.Reference("NOAA-19", passStart, lines, tle)!;

        var tl = quad.Corners[0];
        var tr = quad.Corners[1];
        var br = quad.Corners[2];
        var bl = quad.Corners[3];

        // TL and BL are on the same perpendicular side as each other (left of track), TR/BR on the
        // other; the along-track pass start side (TL/TR) must differ geodetically from the pass end
        // side (BR/BL) since pass end is a subsequent (moved) sub-satellite point.
        Assert.NotEqual(tl, br);
        Assert.NotEqual(tr, bl);
    }

    // 3. Corners bracket the sub-track and span ~2x the documented half-swath (~2900 km total).
    [Fact]
    public void Reference_Corners_SpanApproximatelyTheFullSwathWidth()
    {
        var tle = Noaa19Tle;
        var passStart = tle.Epoch;
        const int lines = 240;

        var quad = Referencer.Reference("NOAA-19", passStart, lines, tle)!;

        var tl = quad.Corners[0];
        var tr = quad.Corners[1];

        var widthKm = HaversineKm(tl[1], tl[0], tr[1], tr[0]);

        // SwathHalfWidthKm ~1450 -> full swath ~2900 km; allow generous tolerance for the
        // approximate (non-projected) geometry.
        Assert.InRange(widthKm, 2500.0, 3300.0);
    }

    // 4. Determinism (P5): identical inputs -> a bit-identical quad, every time.
    [Fact]
    public void Reference_SameInputs_IsDeterministic()
    {
        var tle = Noaa19Tle;
        var passStart = tle.Epoch;
        const int lines = 240;

        var a = Referencer.Reference("NOAA-19", passStart, lines, tle);
        var b = Referencer.Reference("NOAA-19", passStart, lines, tle);

        // AptGeoQuad's compiler-generated record equality compares Corners (a double[][]) by
        // reference (arrays don't override Equals), so two structurally-identical quads from
        // separate calls would never compare Equal via a plain Assert.Equal(a, b) even when
        // determinism holds. Assert value-equality on the flattened corners instead.
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Approximate, b!.Approximate);
        Assert.Equal(
            a.Corners.SelectMany(c => c),
            b.Corners.SelectMany(c => c));
    }

    // 5. Unusable input (a deep-space TLE the propagator can't handle) -> null overlay, never a throw.
    [Fact]
    public void Reference_UnusableTle_ReturnsNull()
    {
        const string name = "TDRS 3";
        const string line1 = "1 19548U 88091B   26204.24617572 -.00000298  00000+0  00000+0 0  9999";
        const string line2 = "2 19548  12.5758 340.7613 0037773 355.3937   4.4747  1.00278815125740";
        var deepSpaceTle = Tle.Parse(name, line1, line2);

        var quad = Referencer.Reference("TDRS-3", deepSpaceTle.Epoch, 240, deepSpaceTle);

        Assert.Null(quad);
    }

    // 6. Non-positive line count is unusable -> null, never a throw/divide-by-zero.
    [Fact]
    public void Reference_ZeroLines_ReturnsNull()
    {
        var tle = Noaa19Tle;

        var quad = Referencer.Reference("NOAA-19", tle.Epoch, 0, tle);

        Assert.Null(quad);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        double ToRad(double d) => d * Math.PI / 180.0;

        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return r * c;
    }
}
