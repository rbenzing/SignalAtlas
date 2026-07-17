using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory alert store (SPEC §4.3). Seeds alerts by running a first-sighting event
/// through the real <see cref="IAnomalyEngine"/>, exercising the actual detection path (SPEC §8.8)
/// without a database. Bounded + thread-safe (SPEC NFR-C3): the live pipeline appends raised alerts
/// from its own thread while REST handlers read concurrently; reads return the newest first.
/// </summary>
public sealed class InMemoryAlertRepository : IAlertRepository, IAlertWriter
{
    /// <summary>Retained-alert cap — recent alerts for the API without unbounded growth.</summary>
    public const int Capacity = 500;

    private readonly LinkedList<Alert> _alerts = new(); // oldest → newest
    private readonly object _sync = new();

    public InMemoryAlertRepository(IAnomalyEngine engine)
    {
        var firstSighting = new AnomalyEvent(
            Time: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
            EmitterId: "EMT-000001",
            DeviceId: "DEV-000001",
            Protocol: "ADS-B",
            PowerDbfs: -20,
            Latitude: 42.36,
            Longitude: -71.06,
            Occupancy: 0.1);

        // A never-before-seen emitter+device → new_emitter (+ new_device) alerts.
        var baseline = new EmitterBaseline(
            KnownEmitter: false, KnownDevice: false, LastProtocol: null,
            LastLatitude: null, LastLongitude: null, BaselinePowerDbfs: null, BaselineOccupancy: null);

        foreach (var a in engine.Evaluate(firstSighting, baseline))
            _alerts.AddLast(a);
    }

    /// <summary>Most-recent <paramref name="limit"/> alerts, newest first (SPEC §9.2 GET /alerts).</summary>
    public IReadOnlyList<Alert> GetAlerts(int limit = 100)
    {
        lock (_sync)
        {
            var result = new List<Alert>(Math.Min(limit, _alerts.Count));
            for (var node = _alerts.Last; node is not null && result.Count < limit; node = node.Previous)
                result.Add(node.Value); // newest first
            return result;
        }
    }

    /// <summary>Appends an anomaly alert raised by the live ingestion pipeline (SPEC §8.8).</summary>
    public void Add(Alert a)
    {
        lock (_sync)
        {
            _alerts.AddLast(a);
            while (_alerts.Count > Capacity)
                _alerts.RemoveFirst();
        }
    }
}
