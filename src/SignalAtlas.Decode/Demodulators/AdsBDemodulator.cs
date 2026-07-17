using SignalAtlas.Domain;

namespace SignalAtlas.Decode.Demodulators;

/// <summary>
/// Mode S extended-squitter (DF17/18) physical-layer demodulator (SPEC §8.4 Tier A). Turns 1090 MHz
/// IQ into candidate 14-byte frames by envelope detection → preamble correlation → PPM bit-slicing.
/// CRC is NOT checked here — <c>AdsBDecoder</c> validates it, so noise candidates are rejected
/// downstream. Pure DSP: deterministic, receive-only, reads only the magnitude envelope (L1/L2/P5).
/// </summary>
public sealed class AdsBDemodulator : IDemodulator
{
    private const long AdsBFreqHz = 1_090_000_000;
    private const int FrameBits = 112;
    private const int FrameBytes = 14;
    private const int PreambleSlots = 16;                    // 8 µs preamble = 16 half-µs slots
    private const int FrameSlots = PreambleSlots + FrameBits * 2; // 240
    private const double LowCeilRatio = 0.5;                 // every non-pulse preamble slot must sit below half the pulse level

    public string Protocol => "ADS-B";

    public IEnumerable<ReadOnlyMemory<byte>> Demodulate(IqBlock block, FeatureVector features)
    {
        // Self-gate: 1090 MHz must sit inside the captured band, else this block isn't ours.
        long half = block.SampleRateHz / 2L;
        if (AdsBFreqHz < block.CenterFreqHz - half || AdsBFreqHz > block.CenterFreqHz + half)
            yield break;

        int hus = (block.SampleRateHz / 1_000_000) / 2; // samples per half-µs slot
        if (hus < 1) yield break;                        // need >= 2 MS/s

        float[] iCh = block.I, qCh = block.Q;
        int n = iCh.Length;
        var mag = new double[n];
        for (int k = 0; k < n; k++) mag[k] = (double)iCh[k] * iCh[k] + (double)qCh[k] * qCh[k];

        int frameSamples = FrameSlots * hus;
        int pos = 0;
        while (pos + frameSamples <= n)
        {
            if (TryPreamble(mag, pos, hus))
            {
                yield return SliceFrame(mag, pos, hus);
                pos += frameSamples;   // consume the frame; resume scanning after it
            }
            else
            {
                pos++;
            }
        }
    }

    // Energy in one half-µs slot = sum of magnitude over its hus samples.
    private static double Chip(double[] mag, int baseSample, int slot, int hus)
    {
        double sum = 0;
        int start = baseSample + slot * hus;
        for (int k = 0; k < hus; k++) sum += mag[start + k];
        return sum;
    }

    // Mode S preamble shape over its 16 half-µs slots: pulses at {0,2,7,9}, everything else quiet.
    // Each pulse must dominate its adjacent gaps AND every non-pulse slot (the inner gaps and the
    // quiet tail 10..15) must sit well below the pulse level. Requiring the full shape — not just a
    // few comparisons — is what stops random noise from producing a spurious preamble at 2 MS/s
    // (hus=1), where each slot is a single squared sample with no intra-slot averaging.
    private static bool TryPreamble(double[] mag, int pos, int hus)
    {
        double c0 = Chip(mag, pos, 0, hus), c1 = Chip(mag, pos, 1, hus),
               c2 = Chip(mag, pos, 2, hus), c3 = Chip(mag, pos, 3, hus),
               c4 = Chip(mag, pos, 4, hus), c5 = Chip(mag, pos, 5, hus),
               c6 = Chip(mag, pos, 6, hus), c7 = Chip(mag, pos, 7, hus),
               c8 = Chip(mag, pos, 8, hus), c9 = Chip(mag, pos, 9, hus);

        // Correct pulse/gap ordering.
        if (!(c0 > c1 && c2 > c1 && c2 > c3 && c7 > c6 && c7 > c8 && c9 > c8))
            return false;

        double high = (c0 + c2 + c7 + c9) / 4.0;
        if (high <= 0) return false;
        double lowCeil = high * LowCeilRatio;

        // Every inner non-pulse slot must be quiet.
        if (c1 >= lowCeil || c3 >= lowCeil || c4 >= lowCeil || c5 >= lowCeil || c6 >= lowCeil || c8 >= lowCeil)
            return false;

        // The rest of the preamble window (slots 10..15) must be quiet too.
        for (int slot = 10; slot < PreambleSlots; slot++)
            if (Chip(mag, pos, slot, hus) >= lowCeil)
                return false;

        return true;
    }

    private static byte[] SliceFrame(double[] mag, int pos, int hus)
    {
        var bytes = new byte[FrameBytes];
        for (int b = 0; b < FrameBits; b++)
        {
            double first = Chip(mag, pos, PreambleSlots + 2 * b, hus);
            double second = Chip(mag, pos, PreambleSlots + 2 * b + 1, hus);
            if (first > second) bytes[b >> 3] |= (byte)(0x80 >> (b & 7)); // '1' = pulse in first half
        }
        return bytes;
    }
}
