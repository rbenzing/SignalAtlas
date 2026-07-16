namespace SignalAtlas.Domain;

/// <summary>
/// A power-spectral-density frame captured for the waterfall (SPEC §8.2, §9.3 spectrum.frame).
/// One power value per bin in dBFS-relative units (G20); bin i covers
/// CenterFreqHz + (i - N/2) * (SampleRateHz / N).
/// </summary>
public sealed record SpectrumFrame(
    DateTimeOffset Time,
    long CenterFreqHz,
    int SampleRateHz,
    double[] PowerDbfs);

/// <summary>
/// A bounded, in-memory ring of recent <see cref="SpectrumFrame"/>s feeding the Spectrum waterfall
/// (SPEC §8.2, §4.4). The live pipeline <see cref="Push"/>es a frame per processed block; the API
/// serves the most-recent <see cref="Recent"/> N. Bounded so the buffer never grows unboundedly
/// (NFR-C3 raw-buffer discipline applied to derived PSD frames).
/// </summary>
public interface ISpectrumBuffer
{
    void Push(SpectrumFrame f);

    /// <summary>Most-recent <paramref name="n"/> frames, newest first.</summary>
    IReadOnlyList<SpectrumFrame> Recent(int n);
}
