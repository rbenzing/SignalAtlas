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
    // SyncTemplate polarity exactly).
    private static readonly byte[] SyncA = { 255, 0, 255, 0, 255, 0, 255 };

    /// <summary>rows: greyscale rows, each length 2080. Returns one IqBlock at centerFreqHz/sampleRateHz.</summary>
    public static IqBlock Modulate(byte[][] rows, long centerFreqHz = 137_100_000, int sampleRateHz = 2_000_000)
    {
        if (sampleRateHz % (int)AudioRate != 0)
            throw new ArgumentException($"sampleRateHz must be a multiple of {AudioRate}.", nameof(sampleRateHz));

        // Continuous word sequence: sync-A + video pixels, one line after another. No gap between
        // lines -- AptDecoder resyncs on the sync-A correlation, not on any assumed silence.
        var words = new List<byte>(rows.Length * (SyncA.Length + LineWidth));
        foreach (var row in rows)
        {
            if (row.Length != LineWidth)
                throw new ArgumentException($"each row must have length {LineWidth}.", nameof(rows));
            words.AddRange(SyncA);
            words.AddRange(row);
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
