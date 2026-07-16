namespace SignalAtlas.Domain;

/// <summary>
/// Read/write access to raw observations (SPEC §7.2 observations). The Collector appends
/// measurements; the pipeline reads the most recent ones. Kept minimal (the one Domain
/// addition for M8a persistence) — richer time-window queries land with retention (§7.6).
/// </summary>
public interface IObservationRepository
{
    void Add(Observation o);
    IReadOnlyList<Observation> GetRecent(int limit);
}
