using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Offline-first in-memory device store (SPEC §4.3). Seeds one device by running a sample decoded
/// identity frame through the real <see cref="IDeviceResolver"/>, so the endpoint exercises the
/// actual decode→determine path (SPEC §8.4) without a database. Live device determination is deferred
/// (needs the IQ→bits demodulators), so when a real device is streaming the seed is cleared and this
/// store is empty until decode lands.
/// </summary>
public sealed class InMemoryDeviceRepository : IDeviceRepository, IDemoSeedStore
{
    private readonly List<Device> _devices;
    private readonly object _sync = new();

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

    public IReadOnlyList<Device> GetDevices(int limit = 100)
    {
        lock (_sync)
            return _devices.Take(limit).ToList();
    }

    private const int MaxDevices = 2000;

    /// <summary>Idempotent, newest-first, bounded upsert (SPEC §8.4). Replaces any existing row with
    /// the same id, then prepends, so live re-sightings of one aircraft stay a single entry.</summary>
    public void Upsert(Device device)
    {
        lock (_sync)
        {
            _devices.RemoveAll(d => string.Equals(d.Id, device.Id, StringComparison.Ordinal));
            _devices.Insert(0, device);
            if (_devices.Count > MaxDevices)
                _devices.RemoveRange(MaxDevices, _devices.Count - MaxDevices);
        }
    }

    /// <summary>Drop the seeded demo device so a connected device shows live devices only.</summary>
    public void ClearDemoSeed()
    {
        lock (_sync)
            _devices.Clear();
    }
}
