using System.Buffers.Binary;
using SignalAtlas.Domain;

namespace SignalAtlas.Decode.Decoders;

/// <summary>
/// Decodes the LoRaWAN PHYPayload MAC/frame header, extracting the cleartext <b>DevAddr</b> from
/// the FHDR — SPEC §8.4 Tier A, §4.2 L2. The FRMPayload is AES-128 encrypted and is
/// <b>NEVER</b> parsed (L3, AC-D4). The 4-byte MIC must be present or the frame is Rejected
/// (AC-D6).
/// </summary>
/// <remarks>
/// PHYPayload = MHDR(1) + MACPayload + MIC(4). MACPayload = FHDR + [FPort] + [FRMPayload].
/// FHDR = DevAddr(4, little-endian) + FCtrl(1) + FCnt(2) + FOpts(0..15). MIC is an AES-CMAC and
/// is validated for presence only (no key material on the edge, SPEC §4.2 L3). Only data
/// frame MTypes (uplink/downlink) are decoded.
/// </remarks>
public sealed class LoRaHeaderDecoder : IProtocolDecoder
{
    public string Protocol => "LoRa";

    public bool CanDecode(string protocol) =>
        string.Equals(protocol, Protocol, StringComparison.OrdinalIgnoreCase);

    private const int MHdrLen = 1;
    private const int DevAddrLen = 4;
    private const int FCtrlLen = 1;
    private const int FCntLen = 2;
    private const int MicLen = 4;
    // Smallest valid data frame: MHDR + DevAddr + FCtrl + FCnt + MIC (no FOpts, no FPort/FRMPayload).
    private const int MinFrameLen = MHdrLen + DevAddrLen + FCtrlLen + FCntLen + MicLen;

    public DecodeOutcome Decode(ReadOnlyMemory<byte> frameBytes)
    {
        var frame = frameBytes.Span;
        // MIC presence (AC-D6): a frame too short to hold MHDR+FHDR+MIC has no valid MIC.
        if (frame.Length < MinFrameLen)
            return DecodeOutcome.Rejected("MIC not present (frame truncated)");

        if (!IsDataFrame(frame[0]))
            return DecodeOutcome.Rejected("MHDR MType is not a data frame");

        // DevAddr: cleartext in the FHDR, little-endian. Displayed big-endian (LoRaWAN convention).
        var devAddr = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(MHdrLen, DevAddrLen));
        var devAddrText = devAddr.ToString("X8");

        // FRMPayload is AES-128 encrypted and is NEVER parsed (SPEC §4.2 L3, AC-D4).
        var identifiers = new Dictionary<string, string> { ["devaddr"] = devAddrText };
        var evidence = new List<EvidenceItem>
        {
            new("mic", "present", 1.0),
            new("devaddr", devAddrText, 1.0),
        };

        return DecodeOutcome.Decoded(new DecodedFrame("LoRa", "DataUp", identifiers, 1.0, evidence));
    }

    // Data-frame MTypes (top 3 bits of MHDR): 010 unconfirmed-up, 100 confirmed-up,
    // 011 unconfirmed-down, 101 confirmed-down.
    internal static bool IsDataFrame(byte mhdr)
    {
        var mtype = mhdr >> 5;
        return mtype is 0b010 or 0b011 or 0b100 or 0b101;
    }
}
