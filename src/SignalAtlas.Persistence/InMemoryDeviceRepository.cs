using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory device store (SPEC §4.3). Seeds one device by running a sample decoded
/// identity frame through the real <see cref="IDeviceResolver"/>, so the endpoint exercises the
/// actual decode→determine path (SPEC §8.4) without a database.
/// </summary>
public sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly List<Device> _devices;

    public InMemoryDeviceRepository(IDeviceResolver resolver)
    {
        var adsb = new DecodedFrame(
            Protocol: "ADS-B",
            FrameType: "extended_squitter",
            Identifiers: new Dictionary<string, string> { ["icao"] = "4840D6", ["callsign"] = "KLM1023" },
            DecodeQuality: 1.0,
            Evidence: [new EvidenceItem("crc", "pass", 1.0), new EvidenceItem("df", "17", 0.5)]);

        var device = resolver.Resolve([adsb]);
        _devices = device is null ? [] : [device];
    }

    public IReadOnlyList<Device> GetDevices(int limit = 100) => _devices.Take(limit).ToList();
}
