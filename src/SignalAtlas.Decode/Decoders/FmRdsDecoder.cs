using SignalAtlas.Domain;

namespace SignalAtlas.Decode.Decoders;

/// <summary>
/// FM RDS decoder — Tier A, station-identity metadata only (SPEC §8.4, §18.2).
/// Extracts the PI code (station identifier) and assembles the PS name from group 0A segments;
/// never RadioText/user payload beyond the station name (L2/L3). Blocks failing the RDS
/// (26,16) checkword/offset validation are rejected (AC-D6).
///
/// Demodulated group byte layout: a group is 16 bytes = 4 RDS blocks × 4 bytes. Each block is a
/// big-endian uint32 whose low 26 bits carry the block: bits[25:10] = 16-bit information word,
/// bits[9:0] = 10-bit checkword; the upper 6 bits are 0. Blocks are ordered A, B, C, D. The
/// checkword is the RDS (26,16) cyclic remainder (g(x)=x^10+x^8+x^7+x^5+x^4+x^3+1, 0x5B9) XOR the
/// per-block offset word (A=0x0FC, B=0x198, C=0x168, D=0x1B4). The Decode input is one or more
/// concatenated groups (length a multiple of 16); PS is assembled across group-0A segments.
/// </summary>
public sealed class FmRdsDecoder : IProtocolDecoder
{
    private const int GroupBytes = 16;
    private const int Poly = 0x5B9; // RDS (26,16) generator, low 11 bits.
    private static readonly int[] Offsets = { 0x0FC, 0x198, 0x168, 0x1B4 }; // A, B, C, D

    public string Protocol => "FM-RDS";

    public bool CanDecode(string protocol) =>
        string.Equals(protocol, Protocol, StringComparison.OrdinalIgnoreCase);

    public DecodeOutcome Decode(ReadOnlyMemory<byte> frameBytes)
    {
        var span = frameBytes.Span;
        if (span.Length == 0 || span.Length % GroupBytes != 0)
            return DecodeOutcome.Rejected(
                $"expected whole 16-byte RDS group(s), got {span.Length} bytes");

        int groups = span.Length / GroupBytes;
        int? pi = null;
        var psChars = new char[8];
        var psSeen = new bool[8];
        Span<int> info = stackalloc int[4];

        for (int g = 0; g < groups; g++)
        {
            var group = span.Slice(g * GroupBytes, GroupBytes);

            for (int blk = 0; blk < 4; blk++)
            {
                int o = blk * 4;
                int word = (group[o] << 24) | (group[o + 1] << 16) | (group[o + 2] << 8) | group[o + 3];
                if ((uint)word > 0x03FFFFFF)
                    return DecodeOutcome.Rejected($"group {g} block {blk} exceeds 26 bits");

                int infoWord = (word >> 10) & 0xFFFF;
                int check = word & 0x3FF;
                int expected = ComputeCheck(infoWord) ^ Offsets[blk];
                if (check != expected) // AC-D6: checkword/offset mismatch -> no frame.
                    return DecodeOutcome.Rejected(
                        $"RDS checkword/offset validation failed (group {g} block {blk})");

                info[blk] = infoWord;
            }

            pi ??= info[0]; // PI code is the block A information word.

            int groupType = (info[1] >> 12) & 0xF;
            int version = (info[1] >> 11) & 0x1; // 0 = version A
            if (groupType == 0 && version == 0)
            {
                int seg = info[1] & 0x3; // segment address selects the PS character pair.
                psChars[seg * 2] = (char)((info[3] >> 8) & 0xFF);
                psChars[seg * 2 + 1] = (char)(info[3] & 0xFF);
                psSeen[seg * 2] = psSeen[seg * 2 + 1] = true;
            }
        }

        string piHex = $"{pi:X4}";
        var identifiers = new Dictionary<string, string> { ["pi"] = piHex };
        var evidence = new List<EvidenceItem>
        {
            new("pi", piHex, 1.0),
            new("checkword_pass", "true", 1.0),
            new("group_type", "0A", 1.0),
        };

        if (Array.Exists(psSeen, s => s))
        {
            string ps = new string(psChars).Replace('\0', ' ').TrimEnd();
            if (ps.Length > 0)
            {
                identifiers["ps"] = ps;
                evidence.Add(new EvidenceItem("ps", ps, 1.0));
            }
        }

        var frame = new DecodedFrame("FM-RDS", "rds_group", identifiers, 1.0, evidence);
        return DecodeOutcome.Decoded(frame);
    }

    /// <summary>RDS (26,16) checkword: 10-bit remainder of the info word times x^10 modulo g(x).</summary>
    private static int ComputeCheck(int info)
    {
        int reg = info << 10;
        for (int i = 25; i >= 10; i--)
            if ((reg & (1 << i)) != 0)
                reg ^= Poly << (i - 10);
        return reg & 0x3FF;
    }
}
