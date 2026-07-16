using SignalAtlas.Domain;

namespace SignalAtlas.Decode.Decoders;

/// <summary>
/// Mode S Extended Squitter (DF17/18) decoder — Tier A, public/unencrypted (SPEC §8.4, §18.2).
/// Operates on a demodulated 14-byte (112-bit) frame; validates the Mode S CRC-24 (generator
/// 0xFFF409 / 25-bit 0x1FFF409) and extracts only identity metadata (ICAO address, callsign) — L2.
/// CRC syndrome must be 0 or the frame is rejected (AC-D6). Payload fields are never invented (L3).
/// </summary>
public sealed class AdsBDecoder : IProtocolDecoder
{
    private const int FrameBytes = 14;

    // 6-bit ICAO callsign charset (RTCA DO-260B): 1..26 -> A..Z, 32 -> space, 48..57 -> 0..9.
    private const string Charset = "?ABCDEFGHIJKLMNOPQRSTUVWXYZ????? ???????????????0123456789??????";

    public string Protocol => "ADS-B";

    public bool CanDecode(string protocol) =>
        string.Equals(protocol, Protocol, StringComparison.OrdinalIgnoreCase);

    public DecodeOutcome Decode(ReadOnlyMemory<byte> frameBytes)
    {
        var msg = frameBytes.Span;
        if (msg.Length != FrameBytes)
            return DecodeOutcome.Rejected(
                $"expected {FrameBytes}-byte (112-bit) Mode S extended squitter, got {msg.Length}");

        // AC-D6: non-zero CRC-24 syndrome -> no frame.
        int syndrome = Crc24(msg);
        if (syndrome != 0)
            return DecodeOutcome.Rejected($"Mode S CRC-24 syndrome non-zero (0x{syndrome:X6})");

        int df = msg[0] >> 3; // Downlink Format = top 5 bits.
        if (df is not (17 or 18))
            return DecodeOutcome.Rejected($"DF{df} is not an extended squitter (DF17/18)");

        string icao = $"{msg[1]:X2}{msg[2]:X2}{msg[3]:X2}";
        int typeCode = msg[4] >> 3; // TC = top 5 bits of the ME field.

        var identifiers = new Dictionary<string, string> { ["icao"] = icao };
        var evidence = new List<EvidenceItem>
        {
            new("df", df.ToString(), 1.0),
            new("crc_pass", "syndrome=0", 1.0),
            new("icao", icao, 1.0),
            new("type_code", typeCode.ToString(), 1.0),
        };

        // TC 1..4 = airborne identification: callsign lives in the ME field (bytes 5..10, 48 bits).
        if (typeCode is >= 1 and <= 4)
        {
            string callsign = DecodeCallsign(msg.Slice(5, 6));
            if (callsign.Length > 0)
            {
                identifiers["callsign"] = callsign;
                evidence.Add(new EvidenceItem("callsign", callsign, 1.0));
            }
        }

        var frame = new DecodedFrame("ADS-B", "extended_squitter", identifiers, 1.0, evidence);
        return DecodeOutcome.Decoded(frame);
    }

    /// <summary>
    /// Mode S CRC-24 (generator polynomial 0x1FFF409 = x^24+x^23+...+x^3+x^0). Runs the polynomial
    /// division over the first 88 bits, XORing the 25-bit generator wherever a leading bit is set;
    /// the trailing 24 bits are the syndrome — 0 for a valid frame (and the parity when appended).
    /// </summary>
    private static int Crc24(ReadOnlySpan<byte> msg)
    {
        const int gen = 0x01FFF409;
        Span<byte> d = stackalloc byte[FrameBytes];
        msg.CopyTo(d);
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

    /// <summary>Decode 8 six-bit characters (48 bits) from the ME field into a trimmed callsign.</summary>
    private static string DecodeCallsign(ReadOnlySpan<byte> me6)
    {
        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits = (bits << 8) | me6[i];

        Span<char> chars = stackalloc char[8];
        for (int i = 0; i < 8; i++)
        {
            int sixBit = (int)((bits >> (42 - 6 * i)) & 0x3F);
            char c = Charset[sixBit];
            chars[i] = c == '?' ? ' ' : c; // drop reserved slots
        }
        return new string(chars).TrimEnd();
    }
}
