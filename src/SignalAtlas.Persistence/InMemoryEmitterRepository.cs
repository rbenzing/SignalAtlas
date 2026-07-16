using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory emitter store (SPEC §4.3). Seeded with a few emitters carrying realistic
/// location estimates near Boston (42.36, -71.06) + protocol + evidence so the RF Map plots markers
/// and uncertainty circles (§8.6) WITHOUT live ingestion. The live pipeline upserts correlated
/// emitters on top, keyed idempotently on the deterministic emitter id (SPEC §7.8).
/// </summary>
public sealed class InMemoryEmitterRepository : IEmitterRepository
{
    private readonly Dictionary<string, Emitter> _emitters = new(StringComparer.Ordinal);

    public InMemoryEmitterRepository()
    {
        foreach (var e in Seed())
            _emitters[e.Id] = e;
    }

    private static IEnumerable<Emitter> Seed()
    {
        yield return new Emitter(
            Id: "emitter-adsb-4840D6",
            DeviceId: null,
            Protocol: "ADS-B",
            FreqCenterHz: 1_090_000_000,
            FreqStabilityHz: 2_000,
            EstLatitude: 42.3601,
            EstLongitude: -71.0589,
            EstUncertaintyM: 850.0,
            SignalCount: 24,
            Confidence: 0.88,
            Identifiers: new Dictionary<string, string> { ["icao"] = "4840D6", ["callsign"] = "KLM1023" },
            Evidence:
            [
                new EvidenceItem("decoded_id", "icao=4840D6", 0.9),
                new EvidenceItem("freq_center_hz", "1090000000", 0.5),
            ]);

        yield return new Emitter(
            Id: "emitter-wifi-ap-lobby",
            DeviceId: null,
            Protocol: "Wi-Fi",
            FreqCenterHz: 2_437_000_000,
            FreqStabilityHz: 20_000,
            EstLatitude: 42.3611,
            EstLongitude: -71.0571,
            EstUncertaintyM: 65.0,
            SignalCount: 142,
            Confidence: 0.94,
            Identifiers: new Dictionary<string, string> { ["bssid"] = "A4:2B:B0:11:22:33", ["ssid"] = "atlas-demo" },
            Evidence:
            [
                new EvidenceItem("decoded_id", "bssid=A4:2B:B0:11:22:33", 0.95),
                new EvidenceItem("channel", "6", 0.4),
            ]);

        yield return new Emitter(
            Id: "emitter-lora-915",
            DeviceId: null,
            Protocol: "LoRa",
            FreqCenterHz: 915_000_000,
            FreqStabilityHz: 5_000,
            EstLatitude: 42.3583,
            EstLongitude: -71.0603,
            EstUncertaintyM: 140.0,
            SignalCount: 8,
            Confidence: 0.81,
            Identifiers: new Dictionary<string, string> { ["devaddr"] = "26011F88" },
            Evidence:
            [
                new EvidenceItem("modulation", "CSS", 0.5),
                new EvidenceItem("bandwidth_hz", "125000", 0.42),
            ]);
    }

    public IReadOnlyList<Emitter> All() => _emitters.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

    public void Upsert(Emitter e) => _emitters[e.Id] = e;
}
