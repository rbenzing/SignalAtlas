using SignalAtlas.Domain;

namespace SignalAtlas.Pipeline;

/// <summary>
/// Outcome of one live-ingestion run (SPEC §4.10 real-time edge processing). Counts are the
/// authoritative tally; <see cref="Emitters"/> and <see cref="Alerts"/> are returned in-line
/// because their persistence is a documented follow-up (only observations + signals are stored
/// today — see <see cref="IObservationRepository"/> / <see cref="ISignalWriter"/>).
/// </summary>
public sealed record IngestionResult(
    int ObservationCount,
    int SignalCount,
    int EmitterCount,
    int AlertCount,
    IReadOnlyList<Emitter> Emitters,
    IReadOnlyList<Alert> Alerts);
