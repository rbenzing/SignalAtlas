using System.Buffers.Binary;
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 — LoRaWAN PHY/MAC header decoder (SPEC §8.4 Tier A, AC-D4/D5/D6). Extracts cleartext
/// DevAddr; the AES-128 FRMPayload is never parsed. A missing MIC → Rejected.
/// </summary>
public class DecodeLoRaTests
{
    private static readonly LoRaHeaderDecoder Decoder = new();

    private const uint DevAddr = 0x26011BDA;
    private const string DevAddrText = "26011BDA";

    /// <summary>Builds an unconfirmed data-up PHYPayload: MHDR + FHDR + FPort + FRMPayload + MIC.</summary>
    internal static byte[] BuildUplink(uint devAddr, byte[]? frmPayload = null)
    {
        var frame = new List<byte> { 0x40 }; // MHDR: unconfirmed data up (MType 010).
        var addr = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(addr, devAddr); // DevAddr little-endian in FHDR.
        frame.AddRange(addr);
        frame.Add(0x00);                       // FCtrl (FOptsLen = 0).
        frame.AddRange(new byte[] { 0x01, 0x00 }); // FCnt (LE).
        if (frmPayload is { Length: > 0 })
        {
            frame.Add(0x01);                   // FPort.
            frame.AddRange(frmPayload);        // encrypted FRMPayload (opaque).
        }
        frame.AddRange(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }); // 4-byte MIC.
        return frame.ToArray();
    }

    [Fact]
    public void Decode_ValidUplink_ExtractsDevAddr()
    {
        var outcome = Decoder.Decode(BuildUplink(DevAddr));

        Assert.True(outcome.Success);
        Assert.Equal("LoRa", outcome.Frame!.Protocol);
        Assert.Equal(DevAddrText, outcome.Frame.Identifiers["devaddr"]);
    }

    [Fact]
    public void Decode_ValidUplink_HasNonEmptyEvidence()
    {
        var outcome = Decoder.Decode(BuildUplink(DevAddr));
        Assert.NotEmpty(outcome.Frame!.Evidence);
    }

    [Fact]
    public void Decode_MissingMic_IsRejected()
    {
        var full = BuildUplink(DevAddr);
        var truncated = full[..^3]; // drop most of the MIC → MIC not present.

        var outcome = Decoder.Decode(truncated);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
    }

    [Fact]
    public void Decode_NonDataMType_IsRejected()
    {
        var frame = BuildUplink(DevAddr);
        frame[0] = 0x00; // MHDR MType = join-request, not a data frame.

        var outcome = Decoder.Decode(frame);
        Assert.False(outcome.Success);
    }
}
