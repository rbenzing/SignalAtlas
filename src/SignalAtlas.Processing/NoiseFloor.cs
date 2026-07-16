namespace SignalAtlas.Processing;

/// <summary>
/// Noise-floor estimation via per-bin median (SPEC §8.2). The median is robust to a single
/// large burst (AC-P4): a lone spike among many bins does not move it.
/// </summary>
public static class NoiseFloor
{
    public static double Median(double[] binsDb)
    {
        if (binsDb.Length == 0) return 0.0;
        var sorted = (double[])binsDb.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }
}
