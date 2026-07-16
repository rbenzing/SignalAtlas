namespace SignalAtlas.Domain;

/// <summary>
/// Write access to the signal store (SPEC §7.2 signals). The live ingestion pipeline appends
/// classified signals; the read side stays on <see cref="ISignalRepository"/>. Kept minimal —
/// emitters/alerts produced by a run are returned in the run result and their persistence is a
/// documented follow-up.
/// </summary>
public interface ISignalWriter
{
    void Add(Signal s);
}
