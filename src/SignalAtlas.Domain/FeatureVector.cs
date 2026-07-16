namespace SignalAtlas.Domain;

/// <summary>
/// A power-spectral-density frame: one power value per FFT bin, in dBFS-relative units (G20).
/// Bin i covers frequency CenterFreqHz + (i - N/2) * (SampleRateHz / N).
/// </summary>
public sealed class PsdFrame
{
    public PsdFrame(long centerFreqHz, int sampleRateHz, double[] powerDbfs)
    {
        CenterFreqHz = centerFreqHz;
        SampleRateHz = sampleRateHz;
        PowerDbfs = powerDbfs;
    }

    public long CenterFreqHz { get; }
    public int SampleRateHz { get; }
    public double[] PowerDbfs { get; }
    public int BinCount => PowerDbfs.Length;
    public double BinWidthHz => (double)SampleRateHz / PowerDbfs.Length;
}

/// <summary>
/// Feature set extracted from a detected signal (SPEC §8.2). All power is relative (dBFS, G20).
/// This is the contract shared by the Processing engine (producer) and IClassifier (consumer).
/// </summary>
public sealed record FeatureVector(
    long CenterFreqHz,
    int Bandwidth3dBHz,
    int Bandwidth20dBHz,
    double PeakPowerDbfs,
    double SnrDb,
    int DurationMs,
    double DutyCycle,
    string? ModulationHint);
