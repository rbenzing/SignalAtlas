using System.Buffers.Binary;
using System.Text;
using SignalAtlas.Domain;

namespace SignalAtlas.Decode.Decoders;

/// <summary>
/// Decodes a legacy 802.11 (a/g/p, 20 MHz) management <b>beacon</b> MAC frame into the
/// cleartext identity fields BSSID (address 3) and SSID (tagged element id 0) — SPEC §8.4
/// Tier B, §4.2 L2. The IEEE FCS (CRC-32) trailer must pass or the frame is Rejected (AC-D6).
/// </summary>
/// <remarks>
/// Assumption: legacy beacon only. HT/VHT/HE and 40/80 MHz PHYs are out of scope (SPEC §8.4,
/// §18.2). The frame is the demodulated MAC frame beginning at Frame Control and ending with
/// the 4-byte FCS; no radiotap/PHY prepend is expected.
/// </remarks>
public sealed class WifiBeaconDecoder : IProtocolDecoder
{
    public string Protocol => "Wi-Fi";

    public bool CanDecode(string protocol) =>
        string.Equals(protocol, Protocol, StringComparison.OrdinalIgnoreCase);

    // MAC header: FrameControl(2) Duration(2) Addr1(6) Addr2(6) Addr3/BSSID(6) SeqCtl(2) = 24.
    private const int MacHeaderLen = 24;
    // Fixed beacon body: Timestamp(8) BeaconInterval(2) Capability(2) = 12.
    private const int FixedBodyLen = 12;
    private const int FcsLen = 4;
    private const ushort BeaconFrameControl = 0x0080; // type=mgmt(00) subtype=beacon(1000), little-endian byte0=0x80.

    public DecodeOutcome Decode(ReadOnlyMemory<byte> frameBytes)
    {
        var frame = frameBytes.Span;
        if (frame.Length < MacHeaderLen + FixedBodyLen + FcsLen)
            return DecodeOutcome.Rejected("frame shorter than a legacy beacon");

        // Frame Control little-endian: byte0 carries type+subtype; beacon = 0x80.
        if (frame[0] != (byte)(BeaconFrameControl & 0xFF))
            return DecodeOutcome.Rejected("not a beacon frame");

        // FCS (AC-D6): CRC-32 over the MAC frame, trailer stored little-endian.
        var body = frame[..^FcsLen];
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(frame[^FcsLen..]);
        if (ComputeFcs(body) != expected)
            return DecodeOutcome.Rejected("FCS (CRC-32) mismatch");

        // BSSID = address 3 (bytes 16..22).
        var bssid = FormatMac(frame.Slice(16, 6));

        // Tagged parameters begin after the fixed beacon body; SSID is element id 0.
        string? ssid = null;
        var p = MacHeaderLen + FixedBodyLen;
        var end = frame.Length - FcsLen;
        while (p + 2 <= end)
        {
            int id = frame[p];
            int len = frame[p + 1];
            if (p + 2 + len > end) break;
            if (id == 0x00) // SSID element (cleartext identity only — L2).
            {
                ssid = Encoding.ASCII.GetString(frame.Slice(p + 2, len));
                break;
            }
            p += 2 + len;
        }

        var identifiers = new Dictionary<string, string> { ["bssid"] = bssid };
        if (ssid is not null) identifiers["ssid"] = ssid;

        var evidence = new List<EvidenceItem>
        {
            new("fcs", "pass", 1.0),
            new("bssid", bssid, 1.0),
        };
        if (ssid is not null) evidence.Add(new EvidenceItem("ssid", ssid, 0.8));

        return DecodeOutcome.Decoded(new DecodedFrame("Wi-Fi", "Beacon", identifiers, 1.0, evidence));
    }

    private static string FormatMac(ReadOnlySpan<byte> mac)
    {
        var sb = new StringBuilder(17);
        for (var i = 0; i < mac.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(mac[i].ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>IEEE 802.3/802.11 FCS: CRC-32 (poly 0xEDB88320 reflected, init/xorout 0xFFFFFFFF).</summary>
    public static uint ComputeFcs(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
