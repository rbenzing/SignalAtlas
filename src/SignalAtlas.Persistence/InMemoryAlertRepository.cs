using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory alert store (SPEC §4.3). Seeds alerts by running a first-sighting event
/// through the real <see cref="IAnomalyEngine"/>, exercising the actual detection path (SPEC §8.8)
/// without a database.
/// </summary>
public sealed class InMemoryAlertRepository : IAlertRepository, IAlertWriter
{
    private readonly List<Alert> _alerts;

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

        _alerts = engine.Evaluate(firstSighting, baseline).ToList();
    }

    public IReadOnlyList<Alert> GetAlerts(int limit = 100) => _alerts.Take(limit).ToList();

    /// <summary>Appends an anomaly alert raised by the live ingestion pipeline (SPEC §8.8).</summary>
    public void Add(Alert a) => _alerts.Add(a);
}
