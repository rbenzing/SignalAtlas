namespace SignalAtlas.Domain;

/// <summary>One evidence atom backing a verdict (SPEC §7.3). Rendered verbatim-but-escaped by the UI (P4).</summary>
public sealed record EvidenceItem(string Feature, string Value, double Weight);

/// <summary>
/// A classified observation (SPEC §7.2 signals table). Evidence must be non-empty for any
/// real signal (P4 / NFR-A6); the DB enforces this with a CHECK, the API contract re-asserts it.
/// </summary>
public sealed record Signal(
    long Id,
    DateTimeOffset Time,
    long ObservationId,
    string? EmitterId,
    string? DeviceId,
    string Protocol,
    double Confidence,
    string Classifier,
    IReadOnlyList<EvidenceItem> Evidence,
    long CenterFreqHz,
    int BandwidthHz,
    int? DurationMs,
    IReadOnlyDictionary<string, double> Features);
