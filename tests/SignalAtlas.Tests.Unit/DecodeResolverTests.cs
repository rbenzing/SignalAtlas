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

    // 9. Bug #1 regression: distinct Zigbee nodes on the SAME PAN (real ZigbeeMacDecoder keys
    // src_addr/pan_id — not a hand-made ext_addr/short_addr) must resolve to DIFFERENT primary
    // identifiers. Previously Classify's Zigbee primary keys didn't include src_addr at all, so
    // resolution fell through to pan_id and every node on a PAN collapsed onto one device.
    [Fact]
    public void Resolve_ZigbeeDistinctSrcAddrSamePan_DifferentPrimaryIdentifiers()
    {
        var a = Frame("Zigbee", "Data",
            new Dictionary<string, string> { ["src_addr"] = "AAAA", ["pan_id"] = "1234" });
        var b = Frame("Zigbee", "Data",
            new Dictionary<string, string> { ["src_addr"] = "BBBB", ["pan_id"] = "1234" });

        var deviceA = Resolver.Resolve(new[] { a })!;
        var deviceB = Resolver.Resolve(new[] { b })!;

        Assert.Equal("AAAA", deviceA.PrimaryIdentifier);
        Assert.Equal("BBBB", deviceB.PrimaryIdentifier);
        Assert.NotEqual(deviceA.PrimaryIdentifier, deviceB.PrimaryIdentifier);
        Assert.NotEqual(deviceA.Id, deviceB.Id);
    }

    // 10. Bug #2 regression: the real BleAdvDecoder key is `adva`, not `mac`. A BLE frame carrying
    // only `adva` must resolve with a non-null primary identifier (previously Classify's BLE
    // primary key was {"mac"}, which never matches a real decoder frame → null primary).
    [Fact]
    public void Resolve_BleAdva_NonNullPrimaryIdentifier()
    {
        var frame = Frame("BLE", "ADV_IND",
            new Dictionary<string, string> { ["adva"] = "3c:5a:b4:00:00:01" });

        var device = Resolver.Resolve(new[] { frame })!;

        Assert.Equal("3c:5a:b4:00:00:01", device.PrimaryIdentifier);
    }

    // 11. Bug #22 regression: the vendor/LAA lookup only checked {bssid, mac} — a real BLE frame's
    // `adva` was invisible to it, so a public (non-LAA) advertiser MAC was never vendor-mapped.
    [Fact]
    public void Resolve_BleAdvaPublicMac_VendorMapped()
    {
        var frame = Frame("BLE", "ADV_IND",
            new Dictionary<string, string> { ["adva"] = "3C:5A:B4:00:00:01" });

        var device = Resolver.Resolve(new[] { frame })!;

        Assert.Equal("Google", device.Vendor);
    }

    // 12. Bug #22 regression, LAA side: an adva with the locally-administered bit set is flagged
    // and never vendor-mapped, exactly like bssid/mac.
    [Fact]
    public void Resolve_BleAdvaLaaMac_NullVendorFlaggedInEvidence()
    {
        var frame = Frame("BLE", "ADV_IND",
            new Dictionary<string, string> { ["adva"] = "3E:11:22:33:44:55" });

        var device = Resolver.Resolve(new[] { frame })!;

        Assert.Null(device.Vendor);
        Assert.Contains(device.Evidence, e => e.Feature.Equals("laa_mac", StringComparison.OrdinalIgnoreCase));
    }
}
