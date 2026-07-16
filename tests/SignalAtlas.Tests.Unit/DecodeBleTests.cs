using System.Buffers.Binary;
using System.Text;
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 — BLE advertising decoder (SPEC §8.4 Tier B, AC-D3/D5/D6). Frames carry a valid CRC-24
/// (poly 0x00065B, init 0x555555); random/LAA MACs are flagged and never vendor-mapped.
/// </summary>
public class DecodeBleTests
{
    private static readonly BleAdvDecoder Decoder = new();

    // AdvA is little-endian on the wire; display is the reverse. Public: first octet 0x00.
    private static readonly byte[] PublicAdvaWire = { 0x55, 0x44, 0x33, 0x22, 0x11, 0x00 };
    private const string PublicAdvaText = "00:11:22:33:44:55";

    /// <summary>Builds an ADV_IND PDU (AccessAddr + header + payload + CRC-24).</summary>
    internal static byte[] BuildAdv(byte[] advaWire, bool txAddRandom, string? localName = null,
        ushort? companyId = null, byte[]? mfrExtra = null)
    {
        var payload = new List<byte>();
        payload.AddRange(advaWire); // AdvA.

        if (localName is not null)
        {
            var name = Encoding.ASCII.GetBytes(localName);
            payload.Add((byte)(1 + name.Length));
            payload.Add(0x09); // Complete Local Name.
            payload.AddRange(name);
        }
        if (companyId is not null)
        {
            var data = mfrExtra ?? Array.Empty<byte>();
            payload.Add((byte)(1 + 2 + data.Length));
            payload.Add(0xFF); // Manufacturer Specific Data.
            var cid = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(cid, companyId.Value);
            payload.AddRange(cid);
            payload.AddRange(data);
        }

        var pdu = new List<byte>
        {
            (byte)(0x00 | (txAddRandom ? 0x40 : 0x00)), // header0: ADV_IND type + TxAdd.
            (byte)payload.Count,                        // header1: payload length.
        };
        pdu.AddRange(payload);

        var frame = new List<byte> { 0xD6, 0xBE, 0x89, 0x8E }; // advertising access address.
        frame.AddRange(pdu);
        var crc = BleAdvDecoder.ComputeCrc24(pdu.ToArray());
        frame.Add((byte)(crc & 0xFF));
        frame.Add((byte)((crc >> 8) & 0xFF));
        frame.Add((byte)((crc >> 16) & 0xFF));
        return frame.ToArray();
    }

    [Fact]
    public void Decode_PublicAdv_ExtractsAdvaAndMarksPublic()
    {
        var outcome = Decoder.Decode(BuildAdv(PublicAdvaWire, txAddRandom: false));

        Assert.True(outcome.Success);
        Assert.Equal("BLE", outcome.Frame!.Protocol);
        Assert.Equal(PublicAdvaText, outcome.Frame.Identifiers["adva"]);
        Assert.Equal("public", outcome.Frame.Identifiers["mac_type"]);
    }

    [Fact]
    public void Decode_LocalNameAndMfrData_AreExtracted()
    {
        var outcome = Decoder.Decode(BuildAdv(PublicAdvaWire, txAddRandom: false,
            localName: "AtlasTag", companyId: 0x004C));

        Assert.Equal("AtlasTag", outcome.Frame!.Identifiers["local_name"]);
        Assert.Equal("004C", outcome.Frame.Identifiers["company_id"]);
    }

    [Fact]
    public void Decode_TxAddRandom_FlagsRandomMac()
    {
        var outcome = Decoder.Decode(BuildAdv(PublicAdvaWire, txAddRandom: true));

        Assert.Equal("random", outcome.Frame!.Identifiers["mac_type"]);
        Assert.Contains(outcome.Frame.Evidence, e => e.Value.Contains("random"));
    }

    [Fact]
    public void Decode_LaaBitSet_FlagsRandomMac()
    {
        // First octet (display) 0x02 → LAA bit set, even though TxAdd claims public.
        var laaWire = new byte[] { 0x55, 0x44, 0x33, 0x22, 0x11, 0x02 };
        var outcome = Decoder.Decode(BuildAdv(laaWire, txAddRandom: false));

        Assert.Equal("random", outcome.Frame!.Identifiers["mac_type"]);
    }

    [Fact]
    public void Decode_ValidAdv_HasNonEmptyEvidence()
    {
        var outcome = Decoder.Decode(BuildAdv(PublicAdvaWire, txAddRandom: false));
        Assert.NotEmpty(outcome.Frame!.Evidence);
    }

    [Fact]
    public void Decode_CorruptCrc_IsRejected()
    {
        var frame = BuildAdv(PublicAdvaWire, txAddRandom: false);
        frame[6] ^= 0xFF; // flip an AdvA byte → CRC mismatch.

        var outcome = Decoder.Decode(frame);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
    }
}
