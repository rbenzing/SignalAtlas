namespace SignalAtlas.Domain;

/// <summary>
/// Signal Processing Engine contract (SPEC §8.2): IQ → PSD frame, and detected-signal → features.
/// Deterministic (P5): same samples → identical output. The seam allows a native (FFTW/VOLK)
/// implementation later without changing consumers (addresses review risk R5/A1).
/// </summary>
public interface ISignalProcessor
{
    /// <summary>Compute a power-spectral-density frame from an IQ block (FFT 4096 / Hann / 50% overlap).</summary>
    PsdFrame ComputePsd(IqBlock block);

    /// <summary>Extract the feature vector for the dominant signal in a PSD frame.</summary>
    FeatureVector ExtractFeatures(PsdFrame frame, int durationMs);
}
