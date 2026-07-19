using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory signal store so the API runs with no database (Docker-free CI, SPEC §4.3).
/// Seeded with one signal so the UI has data with no ingestion. Bounded + thread-safe (SPEC NFR-C3):
/// the live pipeline appends per block from its own thread while REST handlers read concurrently, and
/// continuous streaming must not grow unboundedly. Reads return the NEWEST signals first — returning
/// the oldest/seed left the UI showing stale "demo" data even while live classifications streamed in.
/// </summary>
public sealed class InMemorySignalRepository : ISignalRepository, ISignalWriter, IDemoSeedStore
{
    /// <summary>Retained-signal cap — recent classifications for the API without unbounded growth.</summary>
    public const int Capacity = 2000;

    private readonly LinkedList<Signal> _signals = new(); // oldest → newest
    private readonly object _sync = new();

    public InMemorySignalRepository()
    {
        // Seed one signal so the offline UI shows data before any ingestion.
        _signals.AddLast(new Signal(
            Id: 1,
            Time: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
            ObservationId: 1,
            EmitterId: null,
            DeviceId: null,
            Protocol: "LoRa",
            Confidence: 0.92,
            Classifier: "rules-v1",
            Evidence:
            [
                new EvidenceItem("modulation", "CSS", 0.5),
                new EvidenceItem("bandwidth_hz", "125000", 0.42)
            ],
            CenterFreqHz: 915_000_000,
            BandwidthHz: 125_000,
            DurationMs: 350,
            Features: new Dictionary<string, double> { ["snr_db"] = 12.5 }));
    }

    /// <summary>Most-recent <paramref name="limit"/> signals, newest first (SPEC §9.2 GET /signals).</summary>
    public IReadOnlyList<Signal> GetSignals(int limit = 100)
    {
        lock (_sync)
        {
            var result = new List<Signal>(Math.Min(limit, _signals.Count));
            for (var node = _signals.Last; node is not null && result.Count < limit; node = node.Previous)
                result.Add(node.Value); // newest first
            return result;
        }
    }

    /// <summary>Appends a classified signal from the live ingestion pipeline (SPEC §4.10).</summary>
    public void Add(Signal s)
    {
        lock (_sync)
        {
            _signals.AddLast(s);
            while (_signals.Count > Capacity)
                _signals.RemoveFirst();
        }
    }

    /// <summary>Retained-signal count (#8), avoiding a full copy just to count.</summary>
    public int Count()
    {
        lock (_sync)
            return _signals.Count;
    }

    /// <summary>Signals at or after <paramref name="since"/>, most-recent-first (#8).</summary>
    public IReadOnlyList<Signal> GetSince(DateTimeOffset since)
    {
        lock (_sync)
        {
            var result = new List<Signal>();
            for (var node = _signals.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Time >= since)
                    result.Add(node.Value); // newest first
            }
            return result;
        }
    }

    /// <summary>Drop the demo seed (and anything else) so a connected device shows live signals only.</summary>
    public void ClearDemoSeed()
    {
        lock (_sync)
            _signals.Clear();
    }
}
