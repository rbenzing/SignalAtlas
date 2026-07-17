using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory observation store (SPEC §4.3). Empty until the Collector appends.
/// Bounded + thread-safe (SPEC NFR-C3): the live pipeline appends per block from its own thread while
/// REST handlers read concurrently, and continuous streaming must not grow unboundedly — oldest
/// observations are evicted past <see cref="Capacity"/>.
/// </summary>
public sealed class InMemoryObservationRepository : IObservationRepository
{
    /// <summary>Retained-observation cap — recent history for the API without unbounded growth.</summary>
    public const int Capacity = 5000;

    private readonly LinkedList<Observation> _observations = new(); // oldest → newest
    private readonly object _sync = new();

    public void Add(Observation o)
    {
        lock (_sync)
        {
            _observations.AddLast(o);
            while (_observations.Count > Capacity)
                _observations.RemoveFirst();
        }
    }

    public IReadOnlyList<Observation> GetRecent(int limit)
    {
        lock (_sync)
        {
            var result = new List<Observation>(Math.Min(limit, _observations.Count));
            for (var node = _observations.Last; node is not null && result.Count < limit; node = node.Previous)
                result.Add(node.Value); // newest first
            return result;
        }
    }
}
