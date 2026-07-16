using System.Buffers.Binary;
using SignalAtlas.Domain;

namespace SignalAtlas.Decode.Decoders;

/// <summary>
/// Decodes an IEEE 802.15.4 (Zigbee) MAC header into the cleartext identity fields PAN ID and
/// source short/extended address — SPEC §8.4 Tier A, §4.2 L2. The MAC FCS (CRC-16) must pass
/// or the frame is Rejected (AC-D6).
/// </summary>
/// <remarks>
/// MHR = FrameControl(2, LE) + Seq(1) + [DestPanId(2)] + [DestAddr(2|8)] + [SrcPanId(2)] +
/// [SrcAddr(2|8)], then MAC payload, then FCS(2). PAN ID compression reuses the dest PAN as the
/// source PAN. Addresses and PAN IDs are little-endian on the wire (SPEC §8.4). FCS is
/// CRC-16/KERMIT (poly 0x1021 reflected, init 0x0000), transmitted low byte first.
/// </remarks>
public sealed class ZigbeeMacDecoder : IProtocolDecoder
{
    public string Protocol => "Zigbee";

    public bool CanDecode(string protocol) =>
        string.Equals(protocol, Protocol, StringComparison.OrdinalIgnoreCase);

    private const int FcsLen = 2;
    private const int FrameControlLen = 2;
    private const int SeqLen = 1;

    // Addressing modes (FrameControl bits 10-11 dest, 14-15 src).
    private const int AddrModeNone = 0;
    private const int AddrModeShort = 2;
    private const int AddrModeExtended = 3;

    public DecodeOutcome Decode(ReadOnlyMemory<byte> frameBytes)
    {
        var frame = frameBytes.Span;
        if (frame.Length < FrameControlLen + SeqLen + FcsLen)
            return DecodeOutcome.Rejected("frame shorter than a MAC header");

        // FCS (AC-D6): CRC-16 over the frame, trailer stored low byte first.
        var body = frame[..^FcsLen];
        var expected = (ushort)(frame[^2] | (frame[^1] << 8));
        if (ComputeFcs(body) != expected)
            return DecodeOutcome.Rejected("FCS (CRC-16) mismatch");

        var fc = BinaryPrimitives.ReadUInt16LittleEndian(frame[..FrameControlLen]);
        var panCompression = ((fc >> 6) & 1) != 0;
        var destMode = (fc >> 10) & 0x3;
        var srcMode = (fc >> 14) & 0x3;

        var p = FrameControlLen + SeqLen; // skip FrameControl + Seq.
        var end = frame.Length - FcsLen;

        ushort destPan = 0;
        if (destMode != AddrModeNone)
        {
            if (p + 2 > end) return DecodeOutcome.Rejected("truncated dest PAN");
            destPan = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(p, 2));
            p += 2;
            var destLen = destMode == AddrModeExtended ? 8 : 2;
            if (p + destLen > end) return DecodeOutcome.Rejected("truncated dest address");
            p += destLen; // dest address not part of the identity we emit.
        }

        if (srcMode == AddrModeNone)
            return DecodeOutcome.Rejected("no source address to identify");

        ushort srcPan = destPan;
        if (!panCompression)
        {
            if (p + 2 > end) return DecodeOutcome.Rejected("truncated src PAN");
            srcPan = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(p, 2));
            p += 2;
        }

        string srcAddr;
        if (srcMode == AddrModeExtended)
        {
            if (p + 8 > end) return DecodeOutcome.Rejected("truncated extended src address");
            srcAddr = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(p, 8)).ToString("X16");
        }
        else
        {
            if (p + 2 > end) return DecodeOutcome.Rejected("truncated short src address");
            srcAddr = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(p, 2)).ToString("X4");
        }

        var panText = srcPan.ToString("X4");
        var identifiers = new Dictionary<string, string>
        {
            ["pan_id"] = panText,
            ["src_addr"] = srcAddr,
        };
        var evidence = new List<EvidenceItem>
        {
            new("fcs", "pass", 1.0),
            new("pan_id", panText, 0.8),
            new("src_addr", srcAddr, 1.0),
        };

        return DecodeOutcome.Decoded(new DecodedFrame("Zigbee", "Data", identifiers, 1.0, evidence));
    }

    /// <summary>IEEE 802.15.4 FCS: CRC-16/KERMIT (poly 0x8408 reflected, init 0x0000).</summary>
    public static ushort ComputeFcs(ReadOnlySpan<byte> data)
    {
        ushort crc = 0x0000;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }
        return crc;
    }
}
