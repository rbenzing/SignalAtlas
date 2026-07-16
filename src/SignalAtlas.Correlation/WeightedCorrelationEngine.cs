using SignalAtlas.Domain;

namespace SignalAtlas.Correlation;

/// <summary>
/// Assigns a signal to an existing emitter or mints a new one (SPEC §8.5, G8).
///
/// Decision order:
///  1. <b>Decoded-ID-primary.</b> When the signal carries decoded identifiers, they are the
///     PRIMARY key. A shared identifier value with a same-protocol emitter → near-certain match
///     (score ≈ 1.0), regardless of how far the RF centre has drifted (AC-CR1: ID beats RF).
///  2. A decoded ID that matches nothing known → a NEW emitter — we deliberately do NOT fall
///     back to RF scoring, so a distinct BSSID/ICAO can never be merged into a different-ID
///     emitter that merely happens to sit nearby (AC-CR2).
///  3. <b>No-ID RF fallback.</b> With no decoded identifier, score each same-protocol candidate
///     (protocol is a hard gate) over the features the shared Emitter contract actually exposes —
///     frequency proximity (relative to freq stability) and, when both sides have a position,
///     spatial proximity — and reuse the best if it reaches <see cref="CorrelationOptions.MatchThreshold"/>
///     (AC-CR3); else NEW (AC-CR4).
///
/// Deterministic throughout (P5): candidates are ordered by (score desc, Id asc); near-ties
/// (within <see cref="CorrelationOptions.TieEpsilon"/>) resolve to the lowest Id with the basis
/// recorded in evidence. Every result carries non-empty evidence (AC-CR5, P4).
///
/// DEVIATION FROM §8.5: the section lists bandwidth similarity and temporal proximity as scoring
/// terms, but Domain.Emitter (which we may not modify) stores neither a bandwidth nor a last-seen
/// field. Those weights exist on <see cref="CorrelationOptions"/> for forward-compatibility but are
/// dormant; scoring normalizes only over the active terms (frequency, and spatial when available).
/// </summary>
public sealed class WeightedCorrelationEngine : ICorrelationEngine
{
    private const double MatchScore = 1.0;
    private readonly CorrelationOptions _options;

    public WeightedCorrelationEngine() : this(CorrelationOptions.Default) { }

    public WeightedCorrelationEngine(CorrelationOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public CorrelationResult Correlate(CorrelationInput input, IReadOnlyList<Emitter> existing)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(existing);

        return input.DecodedIdentifiers.Count > 0
            ? CorrelateByDecodedId(input, existing)
            : CorrelateByRf(input, existing);
    }

    // ---- 1 & 2: decoded identifier is the primary key -------------------------------------

    private CorrelationResult CorrelateByDecodedId(CorrelationInput input, IReadOnlyList<Emitter> existing)
    {
        // Deterministic scan (lowest Id wins if two emitters somehow share the identifier).
        foreach (var e in existing.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            if (!SameProtocol(e.Protocol, input.Protocol))
                continue;

            var shared = SharedIdentifier(input.DecodedIdentifiers, e.Identifiers);
            if (shared is not null)
            {
                var evidence = new[]
                {
                    new EvidenceItem("decoded_id_match", $"{shared.Value.Key}={shared.Value.Value}", MatchScore),
                    new EvidenceItem("protocol_match", e.Protocol, MatchScore),
                };
                return Matched(e, MatchScore, evidence);
            }
        }

        // A decoded ID with no known counterpart is, by definition, a new device (AC-CR2).
        var signature = IdentifierSignature(input);
        var reason = $"decoded identifier '{signature}' not seen on any known emitter";
        return NewEmitter(input, DeterministicGuid.From(signature).ToString(), MatchScore, "new_by_decoded_id", reason);
    }

    // ---- 3: no decoded ID → weighted RF fallback ------------------------------------------

    private CorrelationResult CorrelateByRf(CorrelationInput input, IReadOnlyList<Emitter> existing)
    {
        // Protocol is a hard gate: a different protocol can never match.
        var scored = existing
            .Where(e => SameProtocol(e.Protocol, input.Protocol))
            .Select(e => (Emitter: e, Score: RfScore(input, e)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Emitter.Id, StringComparer.Ordinal)
            .ToList();

        var rfSignature = RfSignature(input);

        if (scored.Count == 0)
        {
            return NewEmitter(input, DeterministicGuid.From(rfSignature).ToString(), 0.0,
                "new_no_candidate", $"no same-protocol emitter to correlate ({input.Protocol})");
        }

        var best = scored[0];
        var evidence = ScoreEvidence(input, best.Emitter, best.Score);

        // Document a near-tie: the winner was chosen by (highest score, then lowest Id).
        if (scored.Count > 1 && Math.Abs(best.Score - scored[1].Score) <= _options.TieEpsilon)
        {
            evidence.Add(new EvidenceItem(
                "tie_break",
                $"selected {best.Emitter.Id} over {scored[1].Emitter.Id}: equal score {best.Score:0.####}, lowest Id",
                best.Score));
        }

        if (best.Score >= _options.MatchThreshold)
            return Matched(best.Emitter, best.Score, evidence);

        evidence.Add(new EvidenceItem(
            "below_threshold",
            $"best RF score {best.Score:0.####} < threshold {_options.MatchThreshold:0.####}",
            best.Score));
        return NewEmitter(input, DeterministicGuid.From(rfSignature).ToString(), best.Score,
            "new_below_threshold", $"best candidate {best.Emitter.Id} scored {best.Score:0.####} < {_options.MatchThreshold:0.####}", evidence);
    }

    // ---- scoring ---------------------------------------------------------------------------

    private double RfScore(CorrelationInput input, Emitter e)
    {
        double weightSum = 0, weighted = 0;

        // Frequency proximity — always active (every emitter has a centre + stability).
        var freq = FrequencyScore(input.CenterFreqHz, e.FreqCenterHz, e.FreqStabilityHz);
        weighted += _options.FrequencyWeight * freq;
        weightSum += _options.FrequencyWeight;

        // Spatial proximity — active only when both sides carry a position.
        if (input.Latitude is double ilat && input.Longitude is double ilon &&
            e.EstLatitude is double elat && e.EstLongitude is double elon)
        {
            var tolerance = e.EstUncertaintyM is double u and > 0 ? u : _options.SpatialToleranceMeters;
            var spatial = SpatialScore(ilat, ilon, elat, elon, tolerance);
            weighted += _options.SpatialWeight * spatial;
            weightSum += _options.SpatialWeight;
        }

        // Bandwidth + temporal terms are dormant (no emitter-side data — see class remarks).
        return weightSum > 0 ? weighted / weightSum : 0.0;
    }

    private double FrequencyScore(long inputHz, long emitterHz, int stabilityHz)
    {
        var diff = Math.Abs(inputHz - emitterHz);
        var tolerance = Math.Max(1.0, stabilityHz * _options.FrequencyToleranceMultiple);
        return Math.Max(0.0, 1.0 - diff / tolerance);
    }

    private static double SpatialScore(double lat1, double lon1, double lat2, double lon2, double toleranceM)
    {
        var distance = HaversineMeters(lat1, lon1, lat2, lon2);
        return Math.Max(0.0, 1.0 - distance / toleranceM);
    }

    private List<EvidenceItem> ScoreEvidence(CorrelationInput input, Emitter e, double score)
    {
        var diff = Math.Abs(input.CenterFreqHz - e.FreqCenterHz);
        var evidence = new List<EvidenceItem>
        {
            new("protocol_match", e.Protocol, 1.0),
            new("freq_proximity", $"Δ={diff}Hz vs stability {e.FreqStabilityHz}Hz", _options.FrequencyWeight),
        };
        if (input.Latitude is not null && input.Longitude is not null &&
            e.EstLatitude is double elat && e.EstLongitude is double elon)
        {
            var d = HaversineMeters(input.Latitude.Value, input.Longitude.Value, elat, elon);
            evidence.Add(new EvidenceItem("spatial_proximity", $"{d:0.#}m", _options.SpatialWeight));
        }
        evidence.Add(new EvidenceItem("rf_score", score.ToString("0.####"), score));
        return evidence;
    }

    // ---- result builders -------------------------------------------------------------------

    private static CorrelationResult Matched(Emitter e, double score, IReadOnlyList<EvidenceItem> evidence)
    {
        // Reuse the emitter: bump the count and confidence, attach the correlation evidence.
        var updated = e with
        {
            SignalCount = e.SignalCount + 1,
            Confidence = Math.Clamp(Math.Max(e.Confidence, score), 0.0, 1.0),
            Evidence = evidence,
        };
        return new CorrelationResult(updated, IsNew: false, score, evidence);
    }

    private static CorrelationResult NewEmitter(
        CorrelationInput input, string id, double score, string feature, string reason,
        List<EvidenceItem>? prior = null)
    {
        var evidence = prior ?? new List<EvidenceItem>();
        evidence.Add(new EvidenceItem(feature, reason, score));

        var emitter = new Emitter(
            Id: id,
            DeviceId: null,
            Protocol: input.Protocol,
            FreqCenterHz: input.CenterFreqHz,
            FreqStabilityHz: 0,
            EstLatitude: input.Latitude,
            EstLongitude: input.Longitude,
            EstUncertaintyM: null,
            SignalCount: 1,
            Confidence: Math.Clamp(score, 0.0, 1.0),
            Identifiers: input.DecodedIdentifiers,
            Evidence: evidence);
        return new CorrelationResult(emitter, IsNew: true, score, evidence);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static bool SameProtocol(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static KeyValuePair<string, string>? SharedIdentifier(
        IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        foreach (var kv in a.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (b.TryGetValue(kv.Key, out var value) &&
                string.Equals(value, kv.Value, StringComparison.OrdinalIgnoreCase))
            {
                return kv;
            }
        }
        return null;
    }

    // Stable seed for a decoded-ID new emitter: protocol + identifiers ordered by key.
    private static string IdentifierSignature(CorrelationInput input)
    {
        var ids = string.Join(";", input.DecodedIdentifiers
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{input.Protocol}|{ids}";
    }

    // Stable RF signature per SPEC §8.5 ("$"{protocol}:{centerFreqHz}"").
    private static string RfSignature(CorrelationInput input) =>
        $"{input.Protocol}:{input.CenterFreqHz}";

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6_371_000.0; // Earth radius, metres.
        var dLat = DegToRad(lat2 - lat1);
        var dLon = DegToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegToRad(lat1)) * Math.Cos(DegToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;
}
