using SignalAtlas.Domain;

namespace SignalAtlas.Behavior;

/// <summary>
/// Descriptive behavior classifier (SPEC §8.7, §6.1 P4/P5). Turns a set of <see cref="Sighting"/>s
/// into a <see cref="BehaviorProfile"/> purely from inter-arrival timing + position spread:
/// <list type="bullet">
///   <item><b>Inter-arrival → pattern.</b> Gaps between consecutive (time-sorted) sightings drive the
///   pattern. The coefficient of variation CoV = σ/μ of the gaps measures regularity.</item>
///   <item><b>Periodic</b> — evenly spaced: CoV ≤ <see cref="CoVThreshold"/> (0.25). Tolerates jitter
///   because a σ up to 25 % of the mean still classifies as periodic. <c>PeriodS</c> = mean gap.</item>
///   <item><b>AlwaysOn</b> — near-continuous: gaps are very small relative to the span
///   (mean gap ⁄ span ≤ <see cref="AlwaysOnGapSpanRatio"/> = 0.05, i.e. densely & regularly seen), or the
///   emitter is present with no silence longer than its cadence (duty ≥ <see cref="AlwaysOnDutyThreshold"/>
///   = 0.90) even when jittery.</item>
///   <item><b>Burst</b> — everything else: sparse / irregular arrivals with long silences, and the
///   0- or 1-sighting insufficient-data default.</item>
/// </list>
/// <b>DutyCycle</b> = active coverage ⁄ span, where coverage excludes "silences" — gaps longer than the
/// typical cadence (median gap × (1 + <see cref="JitterTolerance"/>)). Uniformly-recurring emitters have
/// no such silence → duty ≈ 1.0; bursty emitters with long idle periods → low duty.
/// <b>Mobility</b> = <see cref="BehaviorProfile.Mobile"/> when the positional spread (max great-circle
/// distance from the centroid) exceeds <see cref="MobilityThresholdMeters"/> (50 m), else
/// <see cref="BehaviorProfile.Stationary"/>; no positions → Stationary with evidence noting the absence.
/// Deterministic (P5): input is time-sorted first; evidence is always non-empty (P4).
/// </summary>
public sealed class BehaviorEngine : IBehaviorEngine
{
    /// <summary>Max coefficient of variation of gaps still considered evenly-spaced (periodic).</summary>
    public const double CoVThreshold = 0.25;

    /// <summary>Fractional jitter tolerated around the median gap when detecting silences.</summary>
    public const double JitterTolerance = 0.2;

    /// <summary>mean-gap ⁄ span at or below which a regular series is "near-continuous" (always_on).</summary>
    public const double AlwaysOnGapSpanRatio = 0.05;

    /// <summary>Active-coverage fraction of span at or above which presence is treated as always_on.</summary>
    public const double AlwaysOnDutyThreshold = 0.90;

    /// <summary>Positional spread (metres) above which the emitter is classified Mobile.</summary>
    public const double MobilityThresholdMeters = 50.0;

    public BehaviorProfile Profile(IReadOnlyList<Sighting> sightings)
    {
        var ordered = sightings.OrderBy(s => s.Time).ToList();
        var (mobility, mobilityEvidence) = ClassifyMobility(ordered);

        // Edge case: 0 or 1 sighting → insufficient data for an inter-arrival pattern (never throw).
        if (ordered.Count < 2)
        {
            var evidence = new List<EvidenceItem>
            {
                new("sighting_count", ordered.Count.ToString(), ordered.Count),
                new("pattern", "insufficient data (need ≥2 sightings for inter-arrival)", 0.0),
            };
            evidence.AddRange(mobilityEvidence);
            return new BehaviorProfile(BehaviorProfile.Burst, null, mobility, 0.0, evidence);
        }

        // Inter-arrival gaps (seconds) between consecutive sightings.
        var gaps = new double[ordered.Count - 1];
        for (var i = 1; i < ordered.Count; i++)
            gaps[i - 1] = (ordered[i].Time - ordered[i - 1].Time).TotalSeconds;

        var mean = gaps.Average();
        var variance = gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Length;
        var stdDev = Math.Sqrt(variance);
        var cov = mean > 0 ? stdDev / mean : 0.0;
        var span = (ordered[^1].Time - ordered[0].Time).TotalSeconds;

        var median = Median(gaps);
        var silenceThreshold = median * (1.0 + JitterTolerance);
        // Coverage = span minus time spent in silences longer than the typical cadence.
        var coverage = gaps.Where(g => g <= silenceThreshold).Sum();
        var duty = span > 0 ? Math.Clamp(coverage / span, 0.0, 1.0) : 0.0;
        var gapSpanRatio = span > 0 ? mean / span : 0.0;

        var regular = cov <= CoVThreshold;
        var dense = gapSpanRatio <= AlwaysOnGapSpanRatio;
        var nearContinuous = duty >= AlwaysOnDutyThreshold;

        string pattern;
        double? periodS;
        if (regular && dense)
        {
            // Evenly spaced AND seen so frequently that presence is effectively continuous.
            pattern = BehaviorProfile.AlwaysOn;
            periodS = null;
        }
        else if (regular)
        {
            // Evenly spaced but spaced out → a periodic beacon; report the mean gap as its period.
            pattern = BehaviorProfile.Periodic;
            periodS = mean;
        }
        else if (nearContinuous)
        {
            // Irregular yet without long silences → still effectively always-on.
            pattern = BehaviorProfile.AlwaysOn;
            periodS = null;
        }
        else
        {
            pattern = BehaviorProfile.Burst;
            periodS = null;
        }

        var patternEvidence = new List<EvidenceItem>
        {
            new("sighting_count", ordered.Count.ToString(), ordered.Count),
            new("mean_gap_s", mean.ToString("0.###"), mean),
            new("gap_cov", cov.ToString("0.###"), cov),
            new("gap_span_ratio", gapSpanRatio.ToString("0.####"), gapSpanRatio),
            new("duty_cycle", duty.ToString("0.###"), duty),
            new("pattern", pattern, cov),
        };
        patternEvidence.AddRange(mobilityEvidence);

        return new BehaviorProfile(pattern, periodS, mobility, duty, patternEvidence);
    }

    private static (string Mobility, IReadOnlyList<EvidenceItem> Evidence) ClassifyMobility(
        IReadOnlyList<Sighting> ordered)
    {
        var located = ordered
            .Where(s => s.Latitude.HasValue && s.Longitude.HasValue)
            .Select(s => (Lat: s.Latitude!.Value, Lon: s.Longitude!.Value))
            .ToList();

        if (located.Count == 0)
        {
            return (BehaviorProfile.Stationary, new[]
            {
                new EvidenceItem("position_data", "none — mobility defaulted to stationary", 0.0),
            });
        }

        var centroidLat = located.Average(p => p.Lat);
        var centroidLon = located.Average(p => p.Lon);
        var spreadM = located.Max(p => HaversineMeters(centroidLat, centroidLon, p.Lat, p.Lon));

        var mobility = spreadM > MobilityThresholdMeters
            ? BehaviorProfile.Mobile
            : BehaviorProfile.Stationary;

        return (mobility, new[]
        {
            new EvidenceItem("position_spread_m", spreadM.ToString("0.##"), spreadM),
            new EvidenceItem("mobility", mobility, spreadM),
        });
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Great-circle distance in metres between two WGS-84 points.</summary>
    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6_371_000.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                  * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
