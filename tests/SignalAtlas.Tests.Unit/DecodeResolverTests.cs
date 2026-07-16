using SignalAtlas.Decode;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 — device resolution from decoded identity frames (SPEC §8.4). Covers identifier→type+vendor
/// (AC-D2 spirit), random-MAC flagged with null vendor (AC-D3), evidence never empty (AC-D5),
/// empty→null, and deterministic Id (test list: "resolver: identifiers→type+vendor").
/// </summary>
public class DecodeResolverTests
{
    private static readonly DeviceResolver Resolver = new(new OuiLookup());

    private static DecodedFrame Frame(
        string protocol, string frameType, IReadOnlyDictionary<string, string> ids, double quality = 1.0) =>
        new(protocol, frameType, ids, quality, new[] { new EvidenceItem("crc", "pass", 1.0) });

    // 1. Wi-Fi beacon with a real (non-LAA) BSSID → "Wi-Fi AP" + vendor from OUI (AC-D2 spirit).
    [Fact]
    public void Resolve_WifiBeaconRealBssid_TypeAndVendor()
    {
        var frame = Frame("Wi-Fi", "beacon",
            new Dictionary<string, string> { ["bssid"] = "00:00:0C:12:34:56", ["ssid"] = "lab-ap" });

        var device = Resolver.Resolve(new[] { frame })!;

        Assert.Equal("Wi-Fi AP", device.DeviceType);
        Assert.Equal("Wi-Fi", device.Protocol);
        Assert.Equal("00:00:0C:12:34:56", device.PrimaryIdentifier);
        Assert.Equal("Cisco", device.Vendor);
        Assert.Equal("lab-ap", device.Identifiers["ssid"]);
        Assert.NotEmpty(device.Evidence);
    }

    // 2. BLE advertisement with a randomized (LAA) MAC → null vendor + flagged in evidence (AC-D3).
    [Fact]
    public void Resolve_BleRandomMac_NullVendorFlaggedInEvidence()
    {
        var frame = Frame("BLE", "adv",
            new Dictionary<string, string> { ["mac"] = "3E:11:22:33:44:55", ["name"] = "Fit" });

        var device = Resolver.Resolve(new[] { frame })!;

        Assert.Equal("BLE device", device.DeviceType);
        Assert.Null(device.Vendor);
        Assert.Contains(device.Evidence, e =>
            e.Value.Contains("random", StringComparison.OrdinalIgnoreCase) ||
            e.Value.Contains("locally", StringComparison.OrdinalIgnoreCase) ||
            e.Feature.Contains("laa", StringComparison.OrdinalIgnoreCase));
    }

    // 3. ADS-B → "Aircraft"; primary identifier is the ICAO; no MAC → null vendor.
    [Fact]
    public void Resolve_AdsB_AircraftWithIcaoPrimary()
    {
        var frame = Frame("ADS-B", "adsb",
            new Dictionary<string, string> { ["icao"] = "A1B2C3", ["callsign"] = "UAL123" });

        var device = Resolver.Resolve(new[] { frame })!;

        Assert.Equal("Aircraft", device.DeviceType);
        Assert.Equal("A1B2C3", device.PrimaryIdentifier);
        Assert.Null(device.Vendor);
    }

    // 4. FM RDS → "FM broadcast".
    [Fact]
    public void Resolve_Fm_BroadcastType()
    {
        var frame = Frame("FM", "rds",
            new Dictionary<string, string> { ["pi"] = "1AShort", ["ps"] = "KEXP" });

        var device = Resolver.Resolve(new[] { frame })!;

        Assert.Equal("FM broadcast", device.DeviceType);
    }

    // 5. Multiple frames merge their identifiers into one device.
    [Fact]
    public void Resolve_MergesIdentifiersAcrossFrames()
    {
        var f1 = Frame("Wi-Fi", "beacon",
            new Dictionary<string, string> { ["bssid"] = "3C:5A:B4:00:00:01" });
        var f2 = Frame("Wi-Fi", "probe",
            new Dictionary<string, string> { ["ssid"] = "guest" });

        var device = Resolver.Resolve(new[] { f1, f2 })!;

        Assert.Equal("3C:5A:B4:00:00:01", device.Identifiers["bssid"]);
        Assert.Equal("guest", device.Identifiers["ssid"]);
        Assert.Equal("Google", device.Vendor);
    }

    // 6. Empty frame list → null (no device).
    [Fact]
    public void Resolve_EmptyFrames_ReturnsNull()
    {
        Assert.Null(Resolver.Resolve(Array.Empty<DecodedFrame>()));
    }

    // 7. Evidence is never empty (AC-D5) even with a bare identifier and no frame evidence.
    [Fact]
    public void Resolve_EvidenceNeverEmpty()
    {
        var frame = new DecodedFrame("ADS-B", "adsb",
            new Dictionary<string, string> { ["icao"] = "DDEEFF" }, 0.9,
            Array.Empty<EvidenceItem>());

        var device = Resolver.Resolve(new[] { frame })!;

        Assert.NotEmpty(device.Evidence);
    }

    // 8. Id is deterministic from the primary identifier.
    [Fact]
    public void Resolve_IdDeterministicFromPrimaryIdentifier()
    {
        var frame = Frame("ADS-B", "adsb",
            new Dictionary<string, string> { ["icao"] = "A1B2C3" });

        var a = Resolver.Resolve(new[] { frame })!;
        var b = Resolver.Resolve(new[] { frame })!;

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(DeterministicGuid.From("A1B2C3").ToString(), a.Id);
    }
}
