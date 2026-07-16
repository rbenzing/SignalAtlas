using SignalAtlas.Decode.Decoders;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 Tier-A ADS-B decoder (SPEC §8.4). Invariants: AC-D1 (golden frame → ICAO/callsign,
/// CRC/FCS pass), AC-D5 (evidence non-empty), AC-D6 (CRC fail → no frame).
/// </summary>
public class DecodeAdsbTests
{
    private static readonly AdsBDecoder Decoder = new();

    // Canonical DO-260B test vector (the-1090mhz-riddle): DF17, ICAO 4840D6, callsign "KLM1023 ".
    // Its Mode S CRC-24 syndrome is 0, so it doubles as a CRC known-answer sanity check.
    private const string GoldenHex = "8D4840D6202CC371C32CE0576098";

    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    // Independent Mode S CRC-24 (generator x^24..x^0 = 0x1FFF409) used to craft valid frames:
    // zero the 24-bit parity, run the division over the first 88 bits, the remainder IS the parity.
    private static int ModeSCrc(ReadOnlySpan<byte> msg14)
    {
        const int gen = 0x01FFF409;
        Span<byte> d = stackalloc byte[14];
        msg14.CopyTo(d);
        for (int i = 0; i < 88; i++)
        {
            if ((d[i >> 3] & (0x80 >> (i & 7))) != 0)
                for (int b = 0; b < 25; b++)
                    if (((gen >> (24 - b)) & 1) != 0)
                    {
                        int pos = i + b;
                        d[pos >> 3] ^= (byte)(0x80 >> (pos & 7));
                    }
        }
        return (d[11] << 16) | (d[12] << 8) | d[13];
    }

    // Build a DF17 identification frame (TC in 1..4) with correct parity appended.
    private static byte[] CraftIdentification(string icaoHex, int typeCode, string callsign)
    {
        const string charset = "?ABCDEFGHIJKLMNOPQRSTUVWXYZ????? ???????????????0123456789??????";
        var f = new byte[14];
        f[0] = 17 << 3; // DF17
        var icao = Hex(icaoHex);
        f[1] = icao[0]; f[2] = icao[1]; f[3] = icao[2];
        f[4] = (byte)(typeCode << 3); // TC + category 0
        ulong bits = 0;
        var cs = callsign.PadRight(8).Substring(0, 8);
        foreach (var ch in cs)
        {
            int idx = charset.IndexOf(ch);
            bits = (bits << 6) | (uint)(idx < 0 ? 32 : idx);
        }
        for (int i = 0; i < 6; i++)
            f[5 + i] = (byte)((bits >> (40 - 8 * i)) & 0xFF);
        int p = ModeSCrc(f);
        f[11] = (byte)(p >> 16); f[12] = (byte)(p >> 8); f[13] = (byte)p;
        return f;
    }

    // AC-D1 + AC-D5: golden frame → ICAO + callsign, CRC pass, evidence present.
    [Fact]
    public void Decode_GoldenVector_ExtractsIcaoCallsignWithEvidence()
    {
        var outcome = Decoder.Decode(Hex(GoldenHex));

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Frame);
        Assert.Equal("ADS-B", outcome.Frame!.Protocol);
        Assert.Equal("extended_squitter", outcome.Frame.FrameType);
        Assert.Equal("4840D6", outcome.Frame.Identifiers["icao"]);
        Assert.Equal("KLM1023", outcome.Frame.Identifiers["callsign"]);
        Assert.Equal(1.0, outcome.Frame.DecodeQuality);
        Assert.NotEmpty(outcome.Frame.Evidence);
    }

    // AC-D1: a crafted valid frame with computed parity decodes to its known fields.
    [Fact]
    public void Decode_CraftedValidFrame_DecodesFields()
    {
        var frame = CraftIdentification("ABCDEF", typeCode: 4, callsign: "TEST123");
        var outcome = Decoder.Decode(frame);

        Assert.True(outcome.Success);
        Assert.Equal("ABCDEF", outcome.Frame!.Identifiers["icao"]);
        Assert.Equal("TEST123", outcome.Frame.Identifiers["callsign"]);
    }

    // AC-D6: flip one bit → non-zero CRC syndrome → Rejected, no frame produced.
    [Fact]
    public void Decode_CorruptedFrame_Rejected()
    {
        var frame = Hex(GoldenHex);
        frame[5] ^= 0x01; // corrupt a payload bit

        var outcome = Decoder.Decode(frame);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
        Assert.NotNull(outcome.RejectReason);
    }

    // Frame length must be exactly 14 bytes (112-bit extended squitter).
    [Fact]
    public void Decode_WrongLength_Rejected()
    {
        var outcome = Decoder.Decode(new byte[7]);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
    }

    [Fact]
    public void CanDecode_MatchesProtocolCaseInsensitive()
    {
        Assert.True(Decoder.CanDecode("ADS-B"));
        Assert.True(Decoder.CanDecode("ads-b"));
        Assert.False(Decoder.CanDecode("FM-RDS"));
    }
}
