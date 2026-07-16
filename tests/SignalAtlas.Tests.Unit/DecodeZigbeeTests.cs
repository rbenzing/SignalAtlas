using System.Buffers.Binary;
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 — IEEE 802.15.4 (Zigbee) MAC header decoder (SPEC §8.4 Tier A, AC-D5/D6). Extracts
/// cleartext PAN ID + source address; FCS is CRC-16/KERMIT and a corruption → Rejected.
/// </summary>
public class DecodeZigbeeTests
{
    private static readonly ZigbeeMacDecoder Decoder = new();

    private const ushort PanId = 0x1A2B;
    private const string PanIdText = "1A2B";
    private const ushort DestShort = 0x0001;
    private const ushort SrcShort = 0xABCD;
    private const string SrcShortText = "ABCD";

    /// <summary>Data frame, short dest+src addressing, PAN ID compression, valid FCS.</summary>
    internal static byte[] BuildShortAddrData(ushort panId, ushort srcShort, byte[]? payload = null)
    {
        var mhr = new List<byte> { 0x41, 0x88, 0x01 }; // FrameControl 0x8841 (LE) + Seq.
        AddLe16(mhr, panId);       // Dest PAN ID.
        AddLe16(mhr, DestShort);   // Dest short address.
        AddLe16(mhr, srcShort);    // Source short address (PAN compression → no src PAN).
        if (payload is not null) mhr.AddRange(payload);

        var fcs = ZigbeeMacDecoder.ComputeFcs(mhr.ToArray());
        AddLe16(mhr, fcs);
        return mhr.ToArray();
    }

    /// <summary>Data frame, short dest + extended (64-bit) source addressing.</summary>
    internal static byte[] BuildExtendedSrcData(ushort panId, ulong srcExt)
    {
        var mhr = new List<byte> { 0x41, 0xC8, 0x01 }; // FrameControl 0xC841 (src mode = extended).
        AddLe16(mhr, panId);
        AddLe16(mhr, DestShort);
        var ext = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(ext, srcExt);
        mhr.AddRange(ext);
        var fcs = ZigbeeMacDecoder.ComputeFcs(mhr.ToArray());
        AddLe16(mhr, fcs);
        return mhr.ToArray();
    }

    private static void AddLe16(List<byte> b, ushort v)
    {
        b.Add((byte)(v & 0xFF));
        b.Add((byte)(v >> 8));
    }

    [Fact]
    public void Decode_ShortAddrData_ExtractsPanAndSrc()
    {
        var outcome = Decoder.Decode(BuildShortAddrData(PanId, SrcShort));

        Assert.True(outcome.Success);
        Assert.Equal("Zigbee", outcome.Frame!.Protocol);
        Assert.Equal(PanIdText, outcome.Frame.Identifiers["pan_id"]);
        Assert.Equal(SrcShortText, outcome.Frame.Identifiers["src_addr"]);
    }

    [Fact]
    public void Decode_ExtendedSrc_ExtractsSixtyFourBitAddr()
    {
        var outcome = Decoder.Decode(BuildExtendedSrcData(PanId, 0x00124B0001020304UL));

        Assert.True(outcome.Success);
        Assert.Equal("00124B0001020304", outcome.Frame!.Identifiers["src_addr"]);
    }

    [Fact]
    public void Decode_ValidData_HasNonEmptyEvidence()
    {
        var outcome = Decoder.Decode(BuildShortAddrData(PanId, SrcShort));
        Assert.NotEmpty(outcome.Frame!.Evidence);
    }

    [Fact]
    public void Decode_CorruptFcs_IsRejected()
    {
        var frame = BuildShortAddrData(PanId, SrcShort);
        frame[3] ^= 0xFF; // flip a PAN ID byte → FCS mismatch.

        var outcome = Decoder.Decode(frame);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
    }
}
