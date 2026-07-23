using SignalAtlas.Geospatial;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// SGP4 orbital propagation validation gate (SPEC §8.4 Phase 2 §3/§6, NOAA APT georeference).
/// Correctness here is the highest-risk piece of the georef feature: a wrong propagator would
/// silently mis-place the weather overlay. The gate is the canonical Vallado SGP4 verification
/// vector (TLE #00005, from "Revisiting Spacetrack Report #3" / Spacetrack Report #3), reproduced
/// independently via the `sgp4` Python package (Brandon Rhodes' port of Vallado's official
/// reference C/C++ implementation) at tsince = 0 min:
///   r_TEME = (7022.46529266, -1400.08296755, 0.03995155) km
/// Deep-space (SDP4) is explicitly out of scope (documented in Sgp4.cs); this suite only exercises
/// the near-Earth path, which is all NOAA POES orbits need.
/// </summary>
public class Sgp4Tests
{
    // Vallado canonical near-Earth verification TLE (satellite 00005), Spacetrack Report #3 test case.
    private const string ValladoName = "TEST SAT 00005";
    private const string ValladoLine1 = "1 00005U 58002B   00179.78495062  .00000023  00000-0  28098-4 0  4753";
    private const string ValladoLine2 = "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667";

    // Live NOAA-19 TLE (NORAD 33591) fetched from Celestrak for this validation; a real POES orbit
    // (~870 km, ~102 min period), used only for the physical-sanity check, not the numeric gate.
    private const string Noaa19Name = "NOAA 19";
    private const string Noaa19Line1 = "1 33591U 09005A   26204.29298328  .00000011  00000+0  29886-4 0  9990";
    private const string Noaa19Line2 = "2 33591  98.9503 275.1908 0012716 289.5371  70.4428 14.13479265899587";

    // 1. THE VALIDATION GATE — Vallado reference vector, tsince = 0 (i.e. exactly at the TLE epoch).
    [Fact]
    public void Propagate_ValladoTle00005_AtEpoch_MatchesReferenceTemePosition()
    {
        var tle = Tle.Parse(ValladoName, ValladoLine1, ValladoLine2);

        var teme = Sgp4.PropagateTeme(tle, tle.Epoch);

        // Reference (km), independently computed via the `sgp4` package (Vallado's own reference
        // engine) for this exact TLE at tsince=0:
        //   x = 7022.46529266, y = -1400.08296755, z = 0.03995155
        const double expectedX = 7022.46529266;
        const double expectedY = -1400.08296755;
        const double expectedZ = 0.03995155;
        // Achieved precision is ~5.7e-9 km (limited by the reference literal's own printed digits,
        // not by this implementation) — 1e-6 km (1 mm) leaves ample margin while still being far
        // tighter than the "a few km" gate the design calls for.
        const double toleranceKm = 0.000001;

        Assert.InRange(Math.Abs(teme.Xkm - expectedX), 0.0, toleranceKm);
        Assert.InRange(Math.Abs(teme.Ykm - expectedY), 0.0, toleranceKm);
        Assert.InRange(Math.Abs(teme.Zkm - expectedZ), 0.0, toleranceKm);
    }

    // 2. Same reference TLE, 720 minutes later — a second independent point on the same vector set
    //    (also cross-checked via the `sgp4` reference package) so the secular/periodic drag terms
    //    (c1..c5, d2..d4, t2cof..t5cof) are exercised, not just the t=0 short-circuit.
    [Fact]
    public void Propagate_ValladoTle00005_At720Minutes_MatchesReferenceTemePosition()
    {
        var tle = Tle.Parse(ValladoName, ValladoLine1, ValladoLine2);

        var teme = Sgp4.PropagateTeme(tle, tle.Epoch.AddMinutes(720.0));

        const double expectedX = -7134.593401193215;
        const double expectedY = 6531.686413336448;
        const double expectedZ = 3260.271864825572;
        const double toleranceKm = 0.000001;

        Assert.InRange(Math.Abs(teme.Xkm - expectedX), 0.0, toleranceKm);
        Assert.InRange(Math.Abs(teme.Ykm - expectedY), 0.0, toleranceKm);
        Assert.InRange(Math.Abs(teme.Zkm - expectedZ), 0.0, toleranceKm);
    }

    // 3. Physical sanity: a real NOAA TLE's sub-point at its own epoch must be a plausible LEO fix.
    [Fact]
    public void Propagate_RealNoaa19Tle_AtEpoch_IsPhysicallySaneSubPoint()
    {
        var tle = Tle.Parse(Noaa19Name, Noaa19Line1, Noaa19Line2);

        var geo = Sgp4.Propagate(tle, tle.Epoch);

        Assert.InRange(geo.LatDeg, -90.0, 90.0);
        Assert.InRange(geo.LonDeg, -180.0, 180.0);
        Assert.InRange(geo.AltKm, 700.0, 900.0); // NOAA POES nominal altitude ~850 km
    }

    // 4. Determinism (P5): same (TLE, time) in → bit-identical result out, every time.
    [Fact]
    public void Propagate_SameTleAndTime_IsDeterministic()
    {
        var tle = Tle.Parse(Noaa19Name, Noaa19Line1, Noaa19Line2);
        var when = tle.Epoch.AddHours(6.25);

        var a = Sgp4.Propagate(tle, when);
        var b = Sgp4.Propagate(tle, when);

        Assert.Equal(a.LatDeg, b.LatDeg);
        Assert.Equal(a.LonDeg, b.LonDeg);
        Assert.Equal(a.AltKm, b.AltKm);
    }

    // 5. Deep-space TLEs (period >= 225 min) are explicitly out of scope — must fail loudly, not
    //    silently return a wrong near-Earth answer. A real geostationary TLE (TDRS 3, ~1436 min
    //    period, mean motion ~1.0 rev/day) is a clean, unambiguous deep-space fixture.
    [Fact]
    public void Propagate_DeepSpaceTle_ThrowsNotSupported()
    {
        const string name = "TDRS 3";
        const string line1 = "1 19548U 88091B   26204.24617572 -.00000298  00000+0  00000+0 0  9999";
        const string line2 = "2 19548  12.5758 340.7613 0037773 355.3937   4.4747  1.00278815125740";
        var tle = Tle.Parse(name, line1, line2);

        Assert.Throws<NotSupportedException>(() => Sgp4.Propagate(tle, tle.Epoch));
    }

    // 6. A known TLE parses to the expected inclination / mean motion (and other elements) — sanity
    //    on the column-offset extraction independent of the propagator.
    [Fact]
    public void Parse_KnownTle_ExtractsExpectedElements()
    {
        var tle = Tle.Parse(Noaa19Name, Noaa19Line1, Noaa19Line2);

        Assert.Equal(33591, tle.NoradId);
        Assert.Equal(98.9503, tle.InclinationDeg, 4);
        Assert.Equal(275.1908, tle.RaanDeg, 4);
        Assert.Equal(0.0012716, tle.Eccentricity, 7);
        Assert.Equal(289.5371, tle.ArgumentOfPerigeeDeg, 4);
        Assert.Equal(70.4428, tle.MeanAnomalyDeg, 4);
        Assert.Equal(14.13479265, tle.MeanMotionRevPerDay, 6);
        Assert.Equal(2026, tle.Epoch.Year);
    }

    // 7. Mod-10 checksum rejects a corrupted line (single-digit flip breaks the checksum).
    [Fact]
    public void Parse_CorruptedChecksum_ThrowsFormatException()
    {
        // Flip the last mean-motion digit (7 -> 8) without touching the checksum char, so the
        // checksum computed over the corrupted line no longer matches the trailing digit.
        const string corruptedLine2 =
            "2 33591  98.9503 275.1908 0012716 289.5371  70.4428 14.13479265899588";

        Assert.Throws<FormatException>(() => Tle.Parse(Noaa19Name, Noaa19Line1, corruptedLine2));
    }

    // 8. Mismatched NORAD ids between line 1 and line 2 are also rejected.
    [Fact]
    public void Parse_MismatchedNoradIds_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => Tle.Parse(ValladoName, ValladoLine1, Noaa19Line2));
    }

    // 9. ParseMany splits a multi-satellite blob (name/line1/line2 triples) into individual Tles.
    [Fact]
    public void ParseMany_MultiSatelliteBlob_ParsesEachTle()
    {
        var blob = string.Join(
            "\n",
            ValladoName, ValladoLine1, ValladoLine2,
            Noaa19Name, Noaa19Line1, Noaa19Line2);

        var tles = Tle.ParseMany(blob);

        Assert.Equal(2, tles.Count);
        Assert.Equal(5, tles[0].NoradId);
        Assert.Equal(33591, tles[1].NoradId);
    }
}
