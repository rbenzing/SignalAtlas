namespace SignalAtlas.Geospatial;

/// <summary>Geodetic sub-point (WGS-84) produced by <see cref="Sgp4.Propagate"/>.</summary>
public readonly record struct GeoPoint(double LatDeg, double LonDeg, double AltKm);

/// <summary>
/// TEME (True Equator, Mean Equinox) Earth-centered inertial state — the raw SGP4 output before
/// the ECEF/geodetic step. Exposed publicly so it can be checked directly against the canonical
/// Vallado verification vectors (SPEC §8.4 Phase 2 validation gate), which are published in TEME
/// km / km-per-second, not geodetic lat/lon.
/// </summary>
public readonly record struct TemeState(
    double Xkm, double Ykm, double Zkm,
    double VxKmPerS, double VyKmPerS, double VzKmPerS);

/// <summary>
/// Standard near-Earth SGP4 orbital propagator (Vallado, Crawford, Hujsak &amp; Kelso, "Revisiting
/// Spacetrack Report #3", AIAA 2006-6753; ported from the WGS72-parameterised reference algorithm
/// in Spacetrack Report #3 / Hoots &amp; Roehrich). TLE mean elements → secular + periodic SGP4
/// corrections → TEME position/velocity (km, km/s) → GMST(utc) rotation → WGS-84 geodetic sub-point.
///
/// <para><b>Scope (documented, not a TODO):</b> this is the <i>near-Earth</i> path only (orbital
/// period &lt; 225 minutes), which is the entire NOAA POES fleet (~800 km / ~100 min orbits, well
/// under the threshold). The deep-space SDP4 branch (12-/24-hour resonance, lunar-solar
/// perturbations for period ≥ 225 min) is NOT implemented — <see cref="Propagate"/> throws
/// <see cref="NotSupportedException"/> for such a TLE rather than silently returning a wrong
/// position, per SPEC §8.4 Phase 2 §3.</para>
///
/// <para>Deterministic (P5): a pure function of (tle, utc) — no clock, no randomness, no shared
/// mutable state between calls.</para>
/// </summary>
public static class Sgp4
{
    // WGS72 constants (Globals.h in the reference implementation) — these are the constants the
    // SGP4 *dynamics* were fitted against; using anything else silently desyncs from the published
    // verification vectors. The final ECI→geodetic step below uses WGS-84 (SPEC §3) instead, which
    // is the modern standard for the map-facing output and introduces only a sub-km difference.
    private const double AeEarthRadii = 1.0;
    private const double Q0 = 120.0;
    private const double S0 = 78.0;
    private const double Mu = 398600.8;
    private const double XkmPerEarthRadius = 6378.135;
    private const double J2 = 1.082616e-3;
    private const double J3 = -2.53881e-6;
    private const double J4 = -1.65597e-6;
    private const double TwoThirds = 2.0 / 3.0;

    private static readonly double Xke =
        60.0 / Math.Sqrt(XkmPerEarthRadius * XkmPerEarthRadius * XkmPerEarthRadius / Mu);
    private static readonly double Ck2 = 0.5 * J2 * AeEarthRadii * AeEarthRadii;
    private static readonly double Ck4 = -0.375 * J4 * Math.Pow(AeEarthRadii, 4.0);
    private static readonly double Qoms2T = Math.Pow((Q0 - S0) / XkmPerEarthRadius, 4.0);
    private static readonly double SConst = AeEarthRadii * (1.0 + S0 / XkmPerEarthRadius);
    private static readonly double A3OvK2 = -J3 / Ck2 * Math.Pow(AeEarthRadii, 3.0);

    private const double TwoPi = 2.0 * Math.PI;
    private const double MinutesPerDay = 1440.0;

    /// <summary>Deep-space threshold (Spacetrack Report #3): period ≥ 225 min needs SDP4, out of scope here.</summary>
    private const double DeepSpacePeriodThresholdMin = 225.0;

    /// <summary>Propagates the TLE to <paramref name="utc"/> and returns the WGS-84 geodetic sub-point.</summary>
    public static GeoPoint Propagate(Tle tle, DateTimeOffset utc)
    {
        var teme = PropagateTeme(tle, utc);
        return TemeToGeodetic(teme, utc);
    }

    /// <summary>
    /// Propagates the TLE to <paramref name="utc"/> and returns the raw TEME position/velocity —
    /// this is what the Vallado verification vectors are published in (see Sgp4Tests).
    /// </summary>
    public static TemeState PropagateTeme(Tle tle, DateTimeOffset utc)
    {
        ArgumentNullException.ThrowIfNull(tle);

        var elements = OrbitalElements.FromTle(tle);
        var consts = NearEarthConstants.From(elements);
        var tsince = (utc - tle.Epoch).TotalMinutes;
        return FindPositionNearEarth(elements, consts, tsince);
    }

    // ---- element extraction + secular-rate constants (Vallado SGP4::Initialise, near-Earth only) ----

    private readonly struct OrbitalElements
    {
        public double MeanAnomaly0 { get; init; }
        public double Raan0 { get; init; }
        public double ArgPerigee0 { get; init; }
        public double Eccentricity0 { get; init; }
        public double Inclination0 { get; init; }
        public double MeanMotion0 { get; init; } // rad/min, un-recovered
        public double BStar { get; init; }
        public double RecoveredMeanMotion { get; init; } // xnodp
        public double RecoveredSemiMajorAxis { get; init; } // aodp
        public double PerigeeKm { get; init; }
        public double PeriodMin { get; init; }

        public static OrbitalElements FromTle(Tle tle)
        {
            var i0 = DegToRad(tle.InclinationDeg);
            var raan0 = DegToRad(tle.RaanDeg);
            var e0 = tle.Eccentricity;
            var omega0 = DegToRad(tle.ArgumentOfPerigeeDeg);
            var m0 = DegToRad(tle.MeanAnomalyDeg);
            var n0 = tle.MeanMotionRevPerDay * TwoPi / MinutesPerDay;

            var cosio = Math.Cos(i0);
            var theta2 = cosio * cosio;
            var x3thm1 = 3.0 * theta2 - 1.0;
            var eosq = e0 * e0;
            var betao2 = 1.0 - eosq;
            var betao = Math.Sqrt(betao2);

            var a1 = Math.Pow(Xke / n0, TwoThirds);
            var temp = 1.5 * Ck2 * x3thm1 / (betao * betao2);
            var del1 = temp / (a1 * a1);
            var a0 = a1 * (1.0 - del1 * (1.0 / 3.0 + del1 * (1.0 + del1 * 134.0 / 81.0)));
            var del0 = temp / (a0 * a0);

            var xnodp = n0 / (1.0 + del0);
            var aodp = a0 / (1.0 - del0);

            var perigeeKm = (aodp * (1.0 - e0) - AeEarthRadii) * XkmPerEarthRadius;
            var periodMin = TwoPi / xnodp;

            return new OrbitalElements
            {
                MeanAnomaly0 = m0,
                Raan0 = raan0,
                ArgPerigee0 = omega0,
                Eccentricity0 = e0,
                Inclination0 = i0,
                MeanMotion0 = n0,
                BStar = tle.BStar,
                RecoveredMeanMotion = xnodp,
                RecoveredSemiMajorAxis = aodp,
                PerigeeKm = perigeeKm,
                PeriodMin = periodMin,
            };
        }
    }

    private readonly struct NearEarthConstants
    {
        public double Cosio { get; init; }
        public double Sinio { get; init; }
        public double X3thm1 { get; init; }
        public double X1mth2 { get; init; }
        public double X7thm1 { get; init; }
        public double Xlcof { get; init; }
        public double Aycof { get; init; }
        public double Eta { get; init; }
        public double C1 { get; init; }
        public double C4 { get; init; }
        public double Xmdot { get; init; }
        public double Omgdot { get; init; }
        public double Xnodot { get; init; }
        public double Xnodcf { get; init; }
        public double T2cof { get; init; }
        public bool UseSimpleModel { get; init; }

        // near-space (non-deep-space) secular/periodic drag terms
        public double C5 { get; init; }
        public double Omgcof { get; init; }
        public double Xmcof { get; init; }
        public double Delmo { get; init; }
        public double Sinmo { get; init; }
        public double D2 { get; init; }
        public double D3 { get; init; }
        public double D4 { get; init; }
        public double T3cof { get; init; }
        public double T4cof { get; init; }
        public double T5cof { get; init; }

        public static NearEarthConstants From(OrbitalElements el)
        {
            if (el.PeriodMin >= DeepSpacePeriodThresholdMin)
            {
                throw new NotSupportedException(
                    $"Deep-space SDP4 propagation (orbital period {el.PeriodMin:F1} min ≥ " +
                    $"{DeepSpacePeriodThresholdMin} min) is out of scope for this near-Earth-only " +
                    "SGP4 propagator (SPEC §8.4 Phase 2 §3). NOAA POES orbits (~100 min) are always " +
                    "near-Earth; this TLE is not.");
            }

            var cosio = Math.Cos(el.Inclination0);
            var sinio = Math.Sin(el.Inclination0);
            var theta2 = cosio * cosio;
            var x3thm1 = 3.0 * theta2 - 1.0;
            var x1mth2 = 1.0 - theta2;
            var x7thm1 = 7.0 * theta2 - 1.0;

            double xlcof = Math.Abs(cosio + 1.0) > 1.5e-12
                ? 0.125 * A3OvK2 * sinio * (3.0 + 5.0 * cosio) / (1.0 + cosio)
                : 0.125 * A3OvK2 * sinio * (3.0 + 5.0 * cosio) / 1.5e-12;
            var aycof = 0.25 * A3OvK2 * sinio;

            var eosq = el.Eccentricity0 * el.Eccentricity0;
            var betao2 = 1.0 - eosq;
            var betao = Math.Sqrt(betao2);

            var useSimpleModel = el.PerigeeKm < 220.0;

            var s4 = SConst;
            var qoms24 = Qoms2T;
            if (el.PerigeeKm < 156.0)
            {
                s4 = el.PerigeeKm - 78.0;
                if (el.PerigeeKm < 98.0)
                    s4 = 20.0;
                qoms24 = Math.Pow((120.0 - s4) * AeEarthRadii / XkmPerEarthRadius, 4.0);
                s4 = s4 / XkmPerEarthRadius + AeEarthRadii;
            }

            var aodp = el.RecoveredSemiMajorAxis;
            var xnodp = el.RecoveredMeanMotion;

            var pinvsq = 1.0 / (aodp * aodp * betao2 * betao2);
            var tsi = 1.0 / (aodp - s4);
            var eta = aodp * el.Eccentricity0 * tsi;
            var etasq = eta * eta;
            var eeta = el.Eccentricity0 * eta;
            var psisq = Math.Abs(1.0 - etasq);
            var coef = qoms24 * Math.Pow(tsi, 4.0);
            var coef1 = coef / Math.Pow(psisq, 3.5);
            var c2 = coef1 * xnodp * (aodp * (1.0 + 1.5 * etasq + eeta * (4.0 + etasq))
                + 0.75 * Ck2 * tsi / psisq * x3thm1 * (8.0 + 3.0 * etasq * (8.0 + etasq)));
            var c1 = el.BStar * c2;
            var c4 = 2.0 * xnodp * coef1 * aodp * betao2
                * (eta * (2.0 + 0.5 * etasq) + el.Eccentricity0 * (0.5 + 2.0 * etasq)
                    - 2.0 * Ck2 * tsi / (aodp * psisq)
                    * (-3.0 * x3thm1 * (1.0 - 2.0 * eeta + etasq * (1.5 - 0.5 * eeta))
                        + 0.75 * x1mth2 * (2.0 * etasq - eeta * (1.0 + etasq)) * Math.Cos(2.0 * el.ArgPerigee0)));

            var theta4 = theta2 * theta2;
            var temp1 = 3.0 * Ck2 * pinvsq * xnodp;
            var temp2 = temp1 * Ck2 * pinvsq;
            var temp3 = 1.25 * Ck4 * pinvsq * pinvsq * xnodp;
            var xmdot = xnodp + 0.5 * temp1 * betao * x3thm1
                + 0.0625 * temp2 * betao * (13.0 - 78.0 * theta2 + 137.0 * theta4);
            var x1m5th = 1.0 - 5.0 * theta2;
            var omgdot = -0.5 * temp1 * x1m5th
                + 0.0625 * temp2 * (7.0 - 114.0 * theta2 + 395.0 * theta4)
                + temp3 * (3.0 - 36.0 * theta2 + 49.0 * theta4);
            var xhdot1 = -temp1 * cosio;
            var xnodot = xhdot1 + (0.5 * temp2 * (4.0 - 19.0 * theta2) + 2.0 * temp3 * (3.0 - 7.0 * theta2)) * cosio;
            var xnodcf = 3.5 * betao2 * xhdot1 * c1;
            var t2cof = 1.5 * c1;

            var c3 = 0.0;
            if (el.Eccentricity0 > 1.0e-4)
                c3 = coef * tsi * A3OvK2 * xnodp * AeEarthRadii * sinio / el.Eccentricity0;

            var c5 = 2.0 * coef1 * aodp * betao2 * (1.0 + 2.75 * (etasq + eeta) + eeta * etasq);
            var omgcof = el.BStar * c3 * Math.Cos(el.ArgPerigee0);

            var xmcof = 0.0;
            if (el.Eccentricity0 > 1.0e-4)
                xmcof = -TwoThirds * coef * el.BStar * AeEarthRadii / eeta;

            var delmo = Math.Pow(1.0 + eta * Math.Cos(el.MeanAnomaly0), 3.0);
            var sinmo = Math.Sin(el.MeanAnomaly0);

            double d2 = 0, d3 = 0, d4 = 0, t3cof = 0, t4cof = 0, t5cof = 0;
            if (!useSimpleModel)
            {
                var c1sq = c1 * c1;
                d2 = 4.0 * aodp * tsi * c1sq;
                var tempD = d2 * tsi * c1 / 3.0;
                d3 = (17.0 * aodp + s4) * tempD;
                d4 = 0.5 * tempD * aodp * tsi * (221.0 * aodp + 31.0 * s4) * c1;
                t3cof = d2 + 2.0 * c1sq;
                t4cof = 0.25 * (3.0 * d3 + c1 * (12.0 * d2 + 10.0 * c1sq));
                t5cof = 0.2 * (3.0 * d4 + 12.0 * c1 * d3 + 6.0 * d2 * d2 + 15.0 * c1sq * (2.0 * d2 + c1sq));
            }

            return new NearEarthConstants
            {
                Cosio = cosio,
                Sinio = sinio,
                X3thm1 = x3thm1,
                X1mth2 = x1mth2,
                X7thm1 = x7thm1,
                Xlcof = xlcof,
                Aycof = aycof,
                Eta = eta,
                C1 = c1,
                C4 = c4,
                Xmdot = xmdot,
                Omgdot = omgdot,
                Xnodot = xnodot,
                Xnodcf = xnodcf,
                T2cof = t2cof,
                UseSimpleModel = useSimpleModel,
                C5 = c5,
                Omgcof = omgcof,
                Xmcof = xmcof,
                Delmo = delmo,
                Sinmo = sinmo,
                D2 = d2,
                D3 = d3,
                D4 = d4,
                T3cof = t3cof,
                T4cof = t4cof,
                T5cof = t5cof,
            };
        }
    }

    // ---- FindPositionSGP4 (Vallado SGP4::FindPositionSGP4) ----

    private static TemeState FindPositionNearEarth(OrbitalElements el, NearEarthConstants c, double tsince)
    {
        var xinc = el.Inclination0;

        var xmdf = el.MeanAnomaly0 + c.Xmdot * tsince;
        var omgadf = el.ArgPerigee0 + c.Omgdot * tsince;
        var xnoddf = el.Raan0 + c.Xnodot * tsince;

        var omega = omgadf;
        var xmp = xmdf;

        var tsq = tsince * tsince;
        var xnode = xnoddf + c.Xnodcf * tsq;
        var tempa = 1.0 - c.C1 * tsince;
        var tempe = el.BStar * c.C4 * tsince;
        var templ = c.T2cof * tsq;

        if (!c.UseSimpleModel)
        {
            var delomg = c.Omgcof * tsince;
            var delm = c.Xmcof * (Math.Pow(1.0 + c.Eta * Math.Cos(xmdf), 3.0) - c.Delmo);
            var temp = delomg + delm;
            xmp += temp;
            omega -= temp;

            var tcube = tsq * tsince;
            var tfour = tsince * tcube;
            tempa = tempa - c.D2 * tsq - c.D3 * tcube - c.D4 * tfour;
            tempe += el.BStar * c.C5 * (Math.Sin(xmp) - c.Sinmo);
            templ += c.T3cof * tcube + tfour * (c.T4cof + tsince * c.T5cof);
        }

        var a = el.RecoveredSemiMajorAxis * tempa * tempa;
        var e = el.Eccentricity0 - tempe;
        var xl = xmp + omega + xnode + el.RecoveredMeanMotion * templ;

        if (e <= -0.001)
            throw new InvalidOperationException("SGP4 error: eccentricity <= -0.001 (invalid propagation).");
        e = Math.Clamp(e, 1.0e-6, 1.0 - 1.0e-6);

        return CalculateFinalPositionVelocity(
            e, a, omega, xl, xnode, xinc,
            c.Xlcof, c.Aycof, c.X3thm1, c.X1mth2, c.X7thm1, c.Cosio, c.Sinio);
    }

    // ---- CalculateFinalPositionVelocity (Vallado SGP4::CalculateFinalPositionVelocity) ----

    private static TemeState CalculateFinalPositionVelocity(
        double e, double a, double omega, double xl, double xnode, double xinc,
        double xlcof, double aycof, double x3thm1, double x1mth2, double x7thm1,
        double cosio, double sinio)
    {
        var beta2 = 1.0 - e * e;
        var xn = Xke / Math.Pow(a, 1.5);

        var axn = e * Math.Cos(omega);
        var temp11 = 1.0 / (a * beta2);
        var xll = temp11 * xlcof * axn;
        var aynl = temp11 * aycof;
        var xlt = xl + xll;
        var ayn = e * Math.Sin(omega) + aynl;
        var elsq = axn * axn + ayn * ayn;

        if (elsq >= 1.0)
            throw new InvalidOperationException("SGP4 error: elsq >= 1.0 (invalid propagation).");

        var capu = (xlt - xnode) % TwoPi; // fmod semantics, matches the reference exactly
        var epw = capu;

        double sinepw = 0.0, cosepw = 0.0, ecose = 0.0, esine = 0.0;
        var maxNewtonRaphson = 1.25 * Math.Abs(Math.Sqrt(elsq));

        for (var i = 0; i < 10; i++)
        {
            sinepw = Math.Sin(epw);
            cosepw = Math.Cos(epw);
            ecose = axn * cosepw + ayn * sinepw;
            esine = axn * sinepw - ayn * cosepw;

            var f = capu - epw + esine;
            if (Math.Abs(f) < 1.0e-12)
                break;

            var fdot = 1.0 - ecose;
            var deltaEpw = f / fdot;

            if (i == 0)
            {
                deltaEpw = Math.Clamp(deltaEpw, -maxNewtonRaphson, maxNewtonRaphson);
            }
            else
            {
                deltaEpw = f / (fdot + 0.5 * esine * deltaEpw);
            }

            epw += deltaEpw;
        }

        var temp21 = 1.0 - elsq;
        var pl = a * temp21;
        if (pl < 0.0)
            throw new InvalidOperationException("SGP4 error: semi-latus rectum < 0 (invalid propagation).");

        var r = a * (1.0 - ecose);
        var temp31 = 1.0 / r;
        var rdot = Xke * Math.Sqrt(a) * esine * temp31;
        var rfdot = Xke * Math.Sqrt(pl) * temp31;
        var temp32 = a * temp31;
        var betal = Math.Sqrt(temp21);
        var temp33 = 1.0 / (1.0 + betal);
        var cosu = temp32 * (cosepw - axn + ayn * esine * temp33);
        var sinu = temp32 * (sinepw - ayn - axn * esine * temp33);
        var u = Math.Atan2(sinu, cosu);
        var sin2u = 2.0 * sinu * cosu;
        var cos2u = 2.0 * cosu * cosu - 1.0;

        var temp41 = 1.0 / pl;
        var temp42 = Ck2 * temp41;
        var temp43 = temp42 * temp41;

        var rk = r * (1.0 - 1.5 * temp43 * betal * x3thm1) + 0.5 * temp42 * x1mth2 * cos2u;
        var uk = u - 0.25 * temp43 * x7thm1 * sin2u;
        var xnodek = xnode + 1.5 * temp43 * cosio * sin2u;
        var xinck = xinc + 1.5 * temp43 * cosio * sinio * cos2u;
        var rdotk = rdot - xn * temp42 * x1mth2 * sin2u;
        var rfdotk = rfdot + xn * temp42 * (x1mth2 * cos2u + 1.5 * x3thm1);

        var sinuk = Math.Sin(uk);
        var cosuk = Math.Cos(uk);
        var sinik = Math.Sin(xinck);
        var cosik = Math.Cos(xinck);
        var sinnok = Math.Sin(xnodek);
        var cosnok = Math.Cos(xnodek);
        var xmx = -sinnok * cosik;
        var xmy = cosnok * cosik;
        var ux = xmx * sinuk + cosnok * cosuk;
        var uy = xmy * sinuk + sinnok * cosuk;
        var uz = sinik * sinuk;
        var vx = xmx * cosuk - cosnok * sinuk;
        var vy = xmy * cosuk - sinnok * sinuk;
        var vz = sinik * cosuk;

        var x = rk * ux * XkmPerEarthRadius;
        var y = rk * uy * XkmPerEarthRadius;
        var z = rk * uz * XkmPerEarthRadius;
        var xdot = (rdotk * ux + rfdotk * vx) * XkmPerEarthRadius / 60.0;
        var ydot = (rdotk * uy + rfdotk * vy) * XkmPerEarthRadius / 60.0;
        var zdot = (rdotk * uz + rfdotk * vz) * XkmPerEarthRadius / 60.0;

        if (rk < 1.0)
            throw new InvalidOperationException("SGP4 error: satellite has decayed (radius < 1 Earth radius).");

        return new TemeState(x, y, z, xdot, ydot, zdot);
    }

    // ---- TEME -> WGS-84 geodetic (GMST rotation + Vallado iterative geodetic latitude) ----

    private static GeoPoint TemeToGeodetic(TemeState teme, DateTimeOffset utc)
    {
        const double wgs84A = 6378.137; // km
        const double wgs84F = 1.0 / 298.257223563;
        const double e2 = wgs84F * (2.0 - wgs84F);

        var gmst = GreenwichMeanSiderealTime(utc);

        var theta = Math.Atan2(teme.Ykm, teme.Xkm);
        var lon = WrapNegPosPi(theta - gmst);

        var r = Math.Sqrt(teme.Xkm * teme.Xkm + teme.Ykm * teme.Ykm);
        var lat = Math.Atan2(teme.Zkm, r);
        var cLat = 1.0;

        for (var i = 0; i < 10; i++)
        {
            var sinLat = Math.Sin(lat);
            cLat = 1.0 / Math.Sqrt(1.0 - e2 * sinLat * sinLat);
            var newLat = Math.Atan2(teme.Zkm + wgs84A * cLat * e2 * sinLat, r);
            if (Math.Abs(newLat - lat) < 1e-10)
            {
                lat = newLat;
                break;
            }
            lat = newLat;
        }

        var alt = r / Math.Cos(lat) - wgs84A * cLat;

        return new GeoPoint(RadToDeg(lat), RadToDeg(lon), alt);
    }

    /// <summary>IAU-1982 GMST from the Julian Date of <paramref name="utc"/> (Vallado, standard formula).</summary>
    private static double GreenwichMeanSiderealTime(DateTimeOffset utc)
    {
        var jd = JulianDate(utc);
        var t = (jd - 2451545.0) / 36525.0;
        var gmstSec = 67310.54841
            + (876600.0 * 3600.0 + 8640184.812866) * t
            + 0.093104 * t * t
            - 6.2e-6 * t * t * t;
        var wrappedSec = Mod(gmstSec, 86400.0);
        return wrappedSec * TwoPi / 86400.0;
    }

    /// <summary>Julian Date (UT1≈UTC) via the standard Vallado calendar-date formula.</summary>
    private static double JulianDate(DateTimeOffset utc)
    {
        var u = utc.ToUniversalTime();
        double y = u.Year;
        double m = u.Month;
        double d = u.Day;
        var dayFraction = u.TimeOfDay.TotalSeconds / 86400.0;

        return 367.0 * y
            - Math.Floor(7.0 * (y + Math.Floor((m + 9.0) / 12.0)) / 4.0)
            + Math.Floor(275.0 * m / 9.0)
            + d + 1721013.5 + dayFraction;
    }

    private static double Mod(double x, double y) => y == 0.0 ? x : x - y * Math.Floor(x / y);

    private static double WrapNegPosPi(double a) => Mod(a + Math.PI, TwoPi) - Math.PI;

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;

    private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;
}
