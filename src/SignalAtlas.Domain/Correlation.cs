namespace SignalAtlas.Domain;

/// <summary>
/// A persistent transmitter (SPEC §7.2 emitters, §8.5). Carries an <c>Identifiers</c> map for
/// decoded-ID-primary matching and a non-empty <c>Evidence</c> list for the correlation decision
/// (AC-CR5) — the latter fixes a gap in the §7.2 DDL, which had no column for correlation evidence.
/// </summary>
public sealed record Emitter(
    string Id,
    string? DeviceId,
    string Protocol,
    long FreqCenterHz,
    int FreqStabilityHz,
    double? EstLatitude,
    double? EstLongitude,
    double? EstUncertaintyM,
    long SignalCount,
    double Confidence,
    IReadOnlyDictionary<string, string> Identifiers,
    IReadOnlyList<EvidenceItem> Evidence);

/// <summary>What the correlation engine needs about a newly classified/decoded signal (SPEC §8.5).</summary>
public sealed record CorrelationInput(
    string Protocol,
    long CenterFreqHz,
    int BandwidthHz,
    double? Latitude,
    double? Longitude,
    DateTimeOffset Time,
    IReadOnlyDictionary<string, string> DecodedIdentifiers);

/// <summary>Outcome of correlating a signal against known emitters.</summary>
public sealed record CorrelationResult(
    Emitter Emitter,
    bool IsNew,
    double Score,
    IReadOnlyList<EvidenceItem> Evidence);

/// <summary>
/// Assigns a signal to an existing emitter or creates a new one (SPEC §8.5, G8).
/// Decoded identifier is the PRIMARY key (matching BSSID/ICAO → near-certain, NFR-A4 ≤2%);
/// otherwise weighted RF scoring (freq proximity, BW, protocol, space, time) ≥ threshold.
/// Every result carries non-empty evidence (AC-CR5).
/// </summary>
public interface ICorrelationEngine
{
    CorrelationResult Correlate(CorrelationInput input, IReadOnlyList<Emitter> existing);
}
