using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>Offline-first in-memory signal store so the API runs with no database (Docker-free CI, SPEC §4.3).</summary>
public sealed class InMemorySignalRepository : ISignalRepository, ISignalWriter
{
    private readonly List<Signal> _signals =
    [
        new Signal(
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
            Features: new Dictionary<string, double> { ["snr_db"] = 12.5 })
    ];

    public IReadOnlyList<Signal> GetSignals(int limit = 100) => _signals.Take(limit).ToList();

    /// <summary>Appends a classified signal from the live ingestion pipeline (SPEC §4.10).</summary>
    public void Add(Signal s) => _signals.Add(s);
}
