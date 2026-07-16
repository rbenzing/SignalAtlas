using SignalAtlas.Domain;

namespace SignalAtlas.Decode.Decoders;

/// <summary>
/// Decodes a BLE advertising PDU into cleartext identity fields: AdvA (advertiser MAC), the
/// Complete Local Name (AD type 0x09) and manufacturer company id (AD type 0xFF) when present —
/// SPEC §8.4 Tier B, §4.2 L2. Random / locally-administered MACs are flagged and never
/// vendor-mapped. The BLE CRC-24 (poly 0x00065B, init 0x555555) must pass or the frame is
/// Rejected (AC-D6).
/// </summary>
/// <remarks>
/// The frame is Access Address(4) + PDU(header 2 + payload) + CRC-24(3). CRC covers the PDU
/// only (SPEC §8.4). Single-channel best-effort capture (§18.2); data-channel hopping is out of
/// scope. Assumption: undirected advertising PDU (ADV_IND / ADV_NONCONN_IND) with AdvA first.
/// </remarks>
public sealed class BleAdvDecoder : IProtocolDecoder
{
    public string Protocol => "BLE";

    public bool CanDecode(string protocol) =>
        string.Equals(protocol, Protocol, StringComparison.OrdinalIgnoreCase);

    private const int AccessAddrLen = 4;
    private const int PduHeaderLen = 2;
    private const int AdvALen = 6;
    private const int CrcLen = 3;
    private const uint Crc24Init = 0x555555u;
    private const uint Crc24Poly = 0x00065Bu;
    private const byte TxAddRandom = 0x40; // PDU header byte 0 bit 6.

    public DecodeOutcome Decode(ReadOnlyMemory<byte> frameBytes)
    {
        var frame = frameBytes.Span;
        if (frame.Length < AccessAddrLen + PduHeaderLen + AdvALen + CrcLen)
            return DecodeOutcome.Rejected("frame shorter than an advertising PDU");

        // PDU = header(2) + payload; CRC-24 covers the PDU only, trailer stored little-endian.
        var pdu = frame[AccessAddrLen..^CrcLen];
        var trailer = frame[^CrcLen..];
        var expected = (uint)(trailer[0] | (trailer[1] << 8) | (trailer[2] << 16));
        if (ComputeCrc24(pdu) != expected)
            return DecodeOutcome.Rejected("CRC-24 mismatch");

        int payloadLen = pdu[1];
        if (PduHeaderLen + payloadLen > pdu.Length || payloadLen < AdvALen)
            return DecodeOutcome.Rejected("advertising payload length invalid");

        var payload = pdu.Slice(PduHeaderLen, payloadLen);
        var advaWire = payload[..AdvALen];

        // AdvA is little-endian on the wire; display is the reverse.
        var advaDisplay = new byte[AdvALen];
        for (var i = 0; i < AdvALen; i++) advaDisplay[i] = advaWire[AdvALen - 1 - i];
        var adva = FormatMac(advaDisplay);

        // Random iff TxAdd set (PDU header) OR the display first octet is locally administered (bit 0x02).
        var txAddRandom = (pdu[0] & TxAddRandom) != 0;
        var laa = (advaDisplay[0] & 0x02) != 0;
        var macType = txAddRandom || laa ? "random" : "public";

        var identifiers = new Dictionary<string, string>
        {
            ["adva"] = adva,
            ["mac_type"] = macType,
        };
        var evidence = new List<EvidenceItem>
        {
            new("crc", "pass", 1.0),
            new("adva", adva, 1.0),
            // Value carries the type so a random/LAA MAC is never vendor-mapped (SPEC §8.4 LAA check).
            new("mac_type", macType, macType == "random" ? 1.0 : 0.5),
        };

        // AD structures follow AdvA: [len][ad_type][data(len-1)]. Cleartext identity only (L2).
        var q = AdvALen;
        while (q + 1 < payload.Length)
        {
            int adLen = payload[q];
            if (adLen == 0 || q + 1 + adLen > payload.Length) break;
            int adType = payload[q + 1];
            var data = payload.Slice(q + 2, adLen - 1);
            switch (adType)
            {
                case 0x09: // Complete Local Name.
                    var name = System.Text.Encoding.ASCII.GetString(data);
                    identifiers["local_name"] = name;
                    evidence.Add(new EvidenceItem("local_name", name, 0.7));
                    break;
                case 0xFF when data.Length >= 2: // Manufacturer Specific Data → company id only.
                    var companyId = (ushort)(data[0] | (data[1] << 8));
                    var cid = companyId.ToString("X4");
                    identifiers["company_id"] = cid;
                    evidence.Add(new EvidenceItem("company_id", cid, 0.6));
                    break;
            }
            q += 1 + adLen;
        }

        return DecodeOutcome.Decoded(new DecodedFrame("BLE", "ADV_IND", identifiers, 1.0, evidence));
    }

    private static string FormatMac(ReadOnlySpan<byte> mac)
    {
        var sb = new System.Text.StringBuilder(17);
        for (var i = 0; i < mac.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(mac[i].ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>BLE CRC-24 (MSB-first, poly 0x00065B) over the PDU. Init 0x555555 for advertising.</summary>
    public static uint ComputeCrc24(ReadOnlySpan<byte> pdu, uint init = Crc24Init)
    {
        var crc = init;
        foreach (var b in pdu)
        {
            crc ^= (uint)b << 16;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x800000u) != 0 ? ((crc << 1) ^ Crc24Poly) & 0xFFFFFFu : (crc << 1) & 0xFFFFFFu;
        }
        return crc & 0xFFFFFFu;
    }
}
