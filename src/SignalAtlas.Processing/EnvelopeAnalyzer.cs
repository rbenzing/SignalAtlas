namespace SignalAtlas.Processing;

/// <summary>
/// Time-domain burst measurement (SPEC §8.2 duration_ms): the longest contiguous run of an
/// on/off amplitude envelope above a threshold, expressed in milliseconds.
/// </summary>
public static class EnvelopeAnalyzer
{
    public static double BurstDurationMs(double[] envelope, int sampleRateHz, double threshold)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        int longest = 0, run = 0;
        foreach (double v in envelope)
        {
            if (v >= threshold) { run++; if (run > longest) longest = run; }
            else run = 0;
        }
        return longest * 1000.0 / sampleRateHz;
    }
}
