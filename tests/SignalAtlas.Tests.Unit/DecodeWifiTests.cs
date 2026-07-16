using System.Buffers.Binary;
using System.Text;
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 — Wi-Fi legacy beacon decoder (SPEC §8.4 Tier B, AC-D2/D5/D6). Frames are crafted with a
/// correct IEEE FCS (CRC-32) and corrupted to exercise Rejected.
/// </summary>
public class DecodeWifiTests
{
    private static readonly WifiBeaconDecoder Decoder = new();

    private static readonly byte[] Bssid = { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
    private const string BssidText = "00:11:22:33:44:55";

    /// <summary>Builds a legacy beacon MAC frame with a valid FCS trailer.</summary>
    internal static byte[] BuildBeacon(byte[] bssid, string ssid, byte[]? extraTags = null)
    {
        var body = new List<byte>();
        // Frame Control (beacon: type=mgmt, subtype=beacon) + Duration.
        body.AddRange(new byte[] { 0x80, 0x00, 0x00, 0x00 });
        // Addr1 = broadcast DA.
        body.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        // Addr2 = SA (== BSSID for an AP beacon).
        body.AddRange(bssid);
        // Addr3 = BSSID.
        body.AddRange(bssid);
        // Sequence Control.
        body.AddRange(new byte[] { 0x00, 0x00 });
        // Fixed beacon body: Timestamp(8) + BeaconInterval(2) + Capability(2).
        body.AddRange(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 0x64, 0x00, 0x01, 0x04 });
        // SSID element: id=0, len, bytes.
        var ssidBytes = Encoding.ASCII.GetBytes(ssid);
        body.Add(0x00);
        body.Add((byte)ssidBytes.Length);
        body.AddRange(ssidBytes);
        if (extraTags is not null) body.AddRange(extraTags);

        var fcs = WifiBeaconDecoder.ComputeFcs(body.ToArray());
        var trailer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, fcs);
        body.AddRange(trailer);
        return body.ToArray();
    }

    [Fact]
    public void Decode_ValidBeacon_ExtractsBssidAndSsid()
    {
        var outcome = Decoder.Decode(BuildBeacon(Bssid, "AtlasNet"));

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Frame);
        Assert.Equal("Wi-Fi", outcome.Frame!.Protocol);
        Assert.Equal(BssidText, outcome.Frame.Identifiers["bssid"]);
        Assert.Equal("AtlasNet", outcome.Frame.Identifiers["ssid"]);
    }

    [Fact]
    public void Decode_ValidBeacon_HasNonEmptyEvidence()
    {
        var outcome = Decoder.Decode(BuildBeacon(Bssid, "AtlasNet"));
        Assert.NotEmpty(outcome.Frame!.Evidence);
    }

    [Fact]
    public void Decode_CorruptFcs_IsRejected()
    {
        var frame = BuildBeacon(Bssid, "AtlasNet");
        frame[10] ^= 0xFF; // flip a byte in Addr2 → FCS mismatch.

        var outcome = Decoder.Decode(frame);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
    }

    [Fact]
    public void CanDecode_MatchesWifiProtocol()
    {
        Assert.True(Decoder.CanDecode("Wi-Fi"));
        Assert.False(Decoder.CanDecode("BLE"));
    }
}
