using SignalAtlas.Decode.Decoders;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 — AC-D4 "encrypted → zero content" guarantee (SPEC §4.2 L3). Each decoder is fed a frame
/// whose payload region carries a distinctive marker; the decoder must surface only cleartext
/// header/identity fields and never lift payload/content bytes into Identifiers.
/// </summary>
public class DecodeL3Tests
{
    // Distinctive payload marker; must never appear in any extracted identifier value.
    private static readonly byte[] Marker = { 0xC0, 0xFF, 0xEE, 0x11, 0x22, 0x33 };
    private const string MarkerHex = "C0FFEE112233";

    private static void AssertNoMarker(DecodedFrame frame)
    {
        foreach (var kv in frame.Identifiers)
            Assert.DoesNotContain(MarkerHex, kv.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertKeysSubsetOf(DecodedFrame frame, params string[] allowed)
    {
        foreach (var key in frame.Identifiers.Keys)
            Assert.Contains(key, allowed);
    }

    [Fact]
    public void Wifi_BodyBytes_NotExtractedAsIdentifiers()
    {
        // A vendor-specific IE (id 0xDD) after the SSID carries the marker; must be ignored.
        var vendorIe = new byte[Marker.Length + 2];
        vendorIe[0] = 0xDD;
        vendorIe[1] = (byte)Marker.Length;
        Marker.CopyTo(vendorIe, 2);

        var frame = new WifiBeaconDecoder().Decode(
            DecodeWifiTests.BuildBeacon(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, "AtlasNet", vendorIe));

        Assert.True(frame.Success);
        AssertKeysSubsetOf(frame.Frame!, "bssid", "ssid");
        AssertNoMarker(frame.Frame!);
    }

    [Fact]
    public void Ble_MfrDataBlob_NotExtractedAsIdentifiers()
    {
        var frame = new BleAdvDecoder().Decode(
            DecodeBleTests.BuildAdv(new byte[] { 0x55, 0x44, 0x33, 0x22, 0x11, 0x00 },
                txAddRandom: false, localName: "AtlasTag", companyId: 0x004C, mfrExtra: Marker));

        Assert.True(frame.Success);
        AssertKeysSubsetOf(frame.Frame!, "adva", "mac_type", "local_name", "company_id");
        AssertNoMarker(frame.Frame!);
    }

    [Fact]
    public void LoRa_FrmPayload_NotExtracted_DevAddrPresent()
    {
        var frame = new LoRaHeaderDecoder().Decode(
            DecodeLoRaTests.BuildUplink(0x26011BDA, frmPayload: Marker));

        Assert.True(frame.Success);
        Assert.Equal("26011BDA", frame.Frame!.Identifiers["devaddr"]);
        AssertKeysSubsetOf(frame.Frame, "devaddr");
        Assert.False(frame.Frame.Identifiers.ContainsKey("frmpayload"));
        Assert.False(frame.Frame.Identifiers.ContainsKey("payload"));
        AssertNoMarker(frame.Frame);
    }

    [Fact]
    public void Zigbee_MacPayload_NotExtractedAsIdentifiers()
    {
        var frame = new ZigbeeMacDecoder().Decode(
            DecodeZigbeeTests.BuildShortAddrData(0x1A2B, 0xABCD, payload: Marker));

        Assert.True(frame.Success);
        AssertKeysSubsetOf(frame.Frame!, "pan_id", "src_addr");
        AssertNoMarker(frame.Frame!);
    }
}
