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
        // Give the seeded aircraft a real fix (Amsterdam-area) so the RF Map has a plottable
        // device with zero hardware — this seed doubles as the map's offline regression fixture.
        if (device is not null)
            device = device with { Latitude = 52.3667, Longitude = 4.9000, AltitudeFt = 38000 };
        _devices = device is null ? [] : [device];
    }

    public IReadOnlyList<Device> GetDevices(int limit = 100)
    {
        lock (_sync)
            return _devices.Take(limit).ToList();
    }

    /// <summary>Retained-device count (#8), avoiding a full copy just to count.</summary>
    public int Count()
    {
        lock (_sync)
            return _devices.Count;
    }

    private const int MaxDevices = 2000;

    /// <summary>Idempotent, newest-first, bounded upsert (SPEC §8.4). Merges into any existing row with
    /// the same id (see <see cref="DeviceMerge"/>) so identity accumulates across blocks instead of
    /// being replaced, then prepends, so live re-sightings of one aircraft stay a single entry.</summary>
    public void Upsert(Device device)
    {
        lock (_sync)
        {
            var existing = _devices.FirstOrDefault(d => string.Equals(d.Id, device.Id, StringComparison.Ordinal));
            var merged = existing is null ? device : DeviceMerge.Merge(existing, device);
            _devices.RemoveAll(d => string.Equals(d.Id, merged.Id, StringComparison.Ordinal));
            _devices.Insert(0, merged);
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
