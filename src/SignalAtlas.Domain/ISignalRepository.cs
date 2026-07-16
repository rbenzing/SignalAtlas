namespace SignalAtlas.Domain;

/// <summary>Read access to stored signals (SPEC §9.2 GET /signals).</summary>
public interface ISignalRepository
{
    IReadOnlyList<Signal> GetSignals(int limit = 100);
}
