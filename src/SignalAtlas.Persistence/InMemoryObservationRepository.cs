using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>Offline-first in-memory observation store (SPEC §4.3). Empty until the Collector appends.</summary>
public sealed class InMemoryObservationRepository : IObservationRepository
{
    private readonly List<Observation> _observations = [];

    public void Add(Observation o) => _observations.Add(o);

    public IReadOnlyList<Observation> GetRecent(int limit) =>
        _observations.OrderByDescending(o => o.Time).Take(limit).ToList();
}
