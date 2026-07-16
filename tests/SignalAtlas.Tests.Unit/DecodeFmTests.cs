using SignalAtlas.Decode.Decoders;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 Tier-A FM RDS decoder (SPEC §8.4). Invariants: valid group → PI (+PS) extracted,
/// evidence non-empty (AC-D5), checkword/offset fail → Rejected (AC-D6).
///
/// Demodulated group byte layout (defined here, mirrored by the decoder):
///   A group is 16 bytes = 4 RDS blocks × 4 bytes. Each block is a big-endian uint32 whose
///   low 26 bits carry the RDS block: bits[25:10] = 16-bit information word, bits[9:0] = 10-bit
///   checkword. The upper 6 bits must be 0. Blocks are ordered A, B, C, D. The checkword is the
///   RDS (26,16) cyclic remainder (g(x)=x^10+x^8+x^7+x^5+x^4+x^3+1, 0x5B9) XOR the per-block
///   offset word (A=0x0FC, B=0x198, C=0x168, D=0x1B4). The Decode input is one or more
///   concatenated groups (length a multiple of 16); PS is assembled across group-0A segments.
/// </summary>
public class DecodeFmTests
{
    private static readonly FmRdsDecoder Decoder = new();

    private const int Poly = 0x5B9;
    private static readonly int[] Offsets = { 0x0FC, 0x198, 0x168, 0x1B4 };

    private static int Check(int info)
    {
        int reg = info << 10;
        for (int i = 25; i >= 10; i--)
            if ((reg & (1 << i)) != 0)
                reg ^= Poly << (i - 10);
        return reg & 0x3FF;
    }

    private static void WriteBlock(byte[] group, int blk, int info)
    {
        int word = (info << 10) | (Check(info) ^ Offsets[blk]);
        group[blk * 4 + 0] = (byte)(word >> 24);
        group[blk * 4 + 1] = (byte)(word >> 16);
        group[blk * 4 + 2] = (byte)(word >> 8);
        group[blk * 4 + 3] = (byte)word;
    }

    // Group 0A: A=PI, B=group-type(0A)+segment, C=alt-freq(0), D=two PS characters.
    private static byte[] Group0A(int pi, int segment, char c1, char c2)
    {
        var g = new byte[16];
        WriteBlock(g, 0, pi);
        WriteBlock(g, 1, segment & 0x3);          // group type 0, version A, segment
        WriteBlock(g, 2, 0);                        // alternative frequencies (ignored)
        WriteBlock(g, 3, (c1 << 8) | c2);
        return g;
    }

    // Four 0A segments spelling an 8-char PS name for PI 0xC479.
    private static byte[] FullStation(int pi, string ps)
    {
        ps = ps.PadRight(8).Substring(0, 8);
        var frame = new byte[16 * 4];
        for (int s = 0; s < 4; s++)
            Array.Copy(Group0A(pi, s, ps[s * 2], ps[s * 2 + 1]), 0, frame, s * 16, 16);
        return frame;
    }

    // Valid groups → PI + PS extracted, evidence present (AC-D5).
    [Fact]
    public void Decode_ValidStation_ExtractsPiAndPs()
    {
        var outcome = Decoder.Decode(FullStation(0xC479, "SIGATLAS"));

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Frame);
        Assert.Equal("FM-RDS", outcome.Frame!.Protocol);
        Assert.Equal("rds_group", outcome.Frame.FrameType);
        Assert.Equal("C479", outcome.Frame.Identifiers["pi"]);
        Assert.Equal("SIGATLAS", outcome.Frame.Identifiers["ps"]);
        Assert.NotEmpty(outcome.Frame.Evidence);
    }

    // A single valid group still yields the PI.
    [Fact]
    public void Decode_SingleGroup_ExtractsPi()
    {
        var outcome = Decoder.Decode(Group0A(0x1234, 0, 'S', 'A'));

        Assert.True(outcome.Success);
        Assert.Equal("1234", outcome.Frame!.Identifiers["pi"]);
    }

    // AC-D6: corrupt a checkword → offset validation fails → Rejected.
    [Fact]
    public void Decode_BadCheckword_Rejected()
    {
        var frame = Group0A(0xC479, 0, 'A', 'B');
        frame[3] ^= 0x01; // flip a checkword bit in block A

        var outcome = Decoder.Decode(frame);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
        Assert.NotNull(outcome.RejectReason);
    }

    [Fact]
    public void Decode_WrongLength_Rejected()
    {
        var outcome = Decoder.Decode(new byte[10]);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
    }

    [Fact]
    public void CanDecode_MatchesProtocolCaseInsensitive()
    {
        Assert.True(Decoder.CanDecode("FM-RDS"));
        Assert.True(Decoder.CanDecode("fm-rds"));
        Assert.False(Decoder.CanDecode("ADS-B"));
    }
}
