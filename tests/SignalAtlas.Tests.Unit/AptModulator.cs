using SignalAtlas.Domain;

namespace SignalAtlas.Decode;

/// <summary>
/// Test-only inverse of <see cref="AptDecoder"/>: synthesizes a NOAA APT audio signal from a
/// greyscale image (each pixel -> 2400 Hz subcarrier amplitude, each line prefixed with the
/// sync-A template) and FM-modulates it onto baseband IQ. This is the "known good" round-trip
/// fixture the decoder is tested against — it deliberately shares the exact word-rate (4160/sec)
/// and audio-rate (20 kHz) accumulator arithmetic that <see cref="AptDecoder"/> uses, so the two
/// are exact inverses with no clock drift between them. Deterministic (P5): no wall clock, no RNG.
/// </summary>
public static class AptModulator
{
    private const double WordRate = 4160.0;
    private const double AudioRate = 20_000.0;
    private const int LineWidth = 2080;
    private const double SubcarrierHz = 2400.0;
    private const double DeviationHz = 8_000.0; // peak FM deviation applied to the composite audio

    // Sync-A: 7 alternating high/low words at the start of every line (must match AptDecoder's
    // SyncTemplate polarity exactly). Starts AND ends low so the sync-to-settle transition is not
    // itself a full-scale jump.
    private static readonly byte[] SyncA = { 0, 255, 0, 255, 0, 255, 0 };

    // Constant low "settle" words between sync-A and the first real video pixel (mirrors real APT's
    // sync -> space -> video structure). AptDecoder's 25-sample envelope boxcar still holds sync-A
    // content immediately after correlation locks; without a settle gap that stale content leaks into
    // the first video word(s) as a transient (must match AptDecoder's SettleWords count exactly).
    private const int SettleWords = 10;

    // Trailing low guard appended after each line's video, before the NEXT line's sync-A. Decoder-side
    // this needs no special handling: it just flows through AptDecoder's normal "seeking" state (its
    // low, non-alternating content correlates poorly against the sync template on its own, so it never
    // triggers a false lock) -- its only purpose is to give the envelope boxcar a flat, low baseline to
    // settle onto before the next sync-A's alternation begins.
    private const int TrailingGuardWords = 10;

    // AptDecoder's correlation-based lock doesn't land on the mathematically exact ideal word (the
    // envelope boxcar's own causal group delay shifts it a little), so its fixed-width collection
    // window isn't naturally centered on this row's true content. Rather than chase that with settle-
    // length tuning (which also controls how much sync-A ringing gets flushed -- a different concern),
    // pad each row with EdgeTrim extra words on both sides, duplicating the row's own first/last pixel.
    // AptDecoder collects the padded width and keeps only the middle LineWidth words, discarding the
    // padding -- so it doesn't matter which direction the lock-timing slop falls, real pixels are never
    // exposed to it. Must match AptDecoder's EdgeTrim exactly.
    private const int EdgeTrim = 4;

    /// <summary>rows: greyscale rows, each length 2080. Returns one IqBlock at centerFreqHz/sampleRateHz.</summary>
    public static IqBlock Modulate(byte[][] rows, long centerFreqHz = 137_100_000, int sampleRateHz = 2_000_000)
    {
        if (sampleRateHz % (int)AudioRate != 0)
            throw new ArgumentException($"sampleRateHz must be a multiple of {AudioRate}.", nameof(sampleRateHz));

        // Continuous word sequence: sync-A + settle + video pixels + a trailing low guard, one line
        // after another. The trailing guard (constant low, matching sync-A's own low start/end level)
        // lets AptDecoder's envelope boxcar fully settle back to baseline before the NEXT line's
        // sync-A alternation begins -- without it, the boxcar is still smeared with the current line's
        // near-white video tail when the next sync-A starts, which visibly skews exactly where the
        // correlation peak (and therefore the lock position) falls, by a few words either way.
        var words = new List<byte>(rows.Length * (SyncA.Length + SettleWords + LineWidth + 2 * EdgeTrim + TrailingGuardWords));
        foreach (var row in rows)
        {
            if (row.Length != LineWidth)
                throw new ArgumentException($"each row must have length {LineWidth}.", nameof(rows));
            words.AddRange(SyncA);
            for (int s = 0; s < SettleWords; s++) words.Add(0);
            for (int s = 0; s < EdgeTrim; s++) words.Add(row[0]);
            words.AddRange(row);
            for (int s = 0; s < EdgeTrim; s++) words.Add(row[LineWidth - 1]);
            for (int s = 0; s < TrailingGuardWords; s++) words.Add(0);
        }

        // Word -> audio-sample assignment via the same fractional accumulator AptDecoder uses to
        // go the other way (audio-sample -> word). Using the identical recurrence (not a closed-form
        // floor()) guarantees the two are exact inverses with zero cumulative drift.
        var audio = new List<float>(words.Count * 5);
        double wordPos = 0.0;
        int wordIdx = 0;
        long sampleIdx = 0;
        while (wordIdx < words.Count)
        {
            double amplitude = words[wordIdx] / 255.0;
            double phase = 2 * Math.PI * SubcarrierHz * sampleIdx / AudioRate;
            audio.Add((float)(amplitude * Math.Sin(phase)));
            sampleIdx++;

            wordPos += WordRate / AudioRate;
            if (wordPos >= 1.0)
            {
                wordPos -= 1.0;
                wordIdx++;
            }
        }

        // FM-modulate: integrate the composite audio as instantaneous frequency deviation, held
        // constant (zero-order hold) over each audio sample's block of raw IQ samples -- this is
        // exactly what FmDiscriminator's box-average decimation inverts, so the round trip is
        // (numerically) lossless up to float rounding.
        int decimation = sampleRateHz / (int)AudioRate;
        int totalSamples = audio.Count * decimation;
        var iArr = new float[totalSamples];
        var qArr = new float[totalSamples];
        double phaseAcc = 0.0;
        int outIdx = 0;
        for (int a = 0; a < audio.Count; a++)
        {
            double freqHz = DeviationHz * audio[a];
            double phaseStep = 2 * Math.PI * freqHz / sampleRateHz;
            for (int j = 0; j < decimation; j++)
            {
                phaseAcc += phaseStep;
                if (phaseAcc > Math.PI) phaseAcc -= 2 * Math.PI;
                else if (phaseAcc < -Math.PI) phaseAcc += 2 * Math.PI;
                iArr[outIdx] = (float)Math.Cos(phaseAcc);
                qArr[outIdx] = (float)Math.Sin(phaseAcc);
                outIdx++;
            }
        }

        return new IqBlock(centerFreqHz, sampleRateHz, iArr, qArr);
    }
}
