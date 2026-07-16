namespace SignalAtlas.Processing;

/// <summary>
/// Fraction of PSD bins at or above a (relative) power threshold — spectral occupancy
/// (SPEC §6.3). Threshold is typically noise floor + 6 dB (SPEC §8.2).
/// </summary>
public sealed class OccupancyCalculator
{
    private readonly double _thresholdDb;

    public OccupancyCalculator(double thresholdDb) => _thresholdDb = thresholdDb;

    public double Fraction(double[] binsDb)
    {
        if (binsDb.Length == 0) return 0.0;
        int above = 0;
        foreach (double db in binsDb)
            if (db >= _thresholdDb) above++;
        return (double)above / binsDb.Length;
    }
}
