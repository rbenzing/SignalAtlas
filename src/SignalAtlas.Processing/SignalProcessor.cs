using SignalAtlas.Domain;

namespace SignalAtlas.Processing;

/// <summary>
/// Deterministic Signal Processing Engine (SPEC §8.2): IQ → Welch PSD frame (FFT 4096 / Hann /
/// 50% overlap) and PSD → <see cref="FeatureVector"/>. Power is dBFS-relative (G20): a full-scale
/// (amplitude 1.0) complex tone lands its peak bin at 0 dBFS. Dependency-free + deterministic (P5).
/// </summary>
public sealed class SignalProcessor : ISignalProcessor
{
    public const int FftSize = 4096;
    private const int Overlap = FftSize / 2;          // 50% overlap (SPEC §8.2)
    private const double FloorLinear = 1e-20;         // -200 dB floor, avoids log(0)
    private const double OccupancyMarginDb = 6.0;     // noise floor + 6 dB (SPEC §8.2)

    private readonly double[] _window;                // Hann window
    private readonly double _coherentGain;            // S1 = Σ w[n]  (dBFS normalization)

    public SignalProcessor()
    {
        _window = new double[FftSize];
        double s1 = 0;
        for (int n = 0; n < FftSize; n++)
        {
            _window[n] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (FftSize - 1)));
            s1 += _window[n];
        }
        _coherentGain = s1;
    }

    public PsdFrame ComputePsd(IqBlock block)
    {
        int count = block.SampleCount;
        var acc = new double[FftSize];                // averaged linear power per FFT bin
        int segments = 0;

        // Method-local scratch buffers, reused across segments within this call. ISignalProcessor
        // is registered as a singleton and ComputePsd is invoked concurrently by per-connection
        // pipelines — these must stay method-local (never instance fields) to remain reentrant.
        var re = new double[FftSize];
        var im = new double[FftSize];

        for (int start = 0; start + FftSize <= count || (segments == 0 && start == 0); start += Overlap)
        {
            for (int n = 0; n < FftSize; n++)
            {
                int idx = start + n;
                double w = _window[n];
                if (idx < count)
                {
                    re[n] = block.I[idx] * w;
                    im[n] = block.Q[idx] * w;
                }
                else
                {
                    // Reused buffer: a prior segment's tail must not leak into a short padded
                    // final segment (a freshly allocated array would have been zero here).
                    re[n] = 0.0;
                    im[n] = 0.0;
                }
            }

            Fft.Forward(re, im);
            for (int k = 0; k < FftSize; k++)
                acc[k] += re[k] * re[k] + im[k] * im[k];

            segments++;
            if (start + FftSize > count) break;       // padded single short segment
        }

        double norm = segments * _coherentGain * _coherentGain;
        var powerDb = new double[FftSize];
        for (int k = 0; k < FftSize; k++)
        {
            double lin = acc[k] / norm;
            powerDb[k] = 10.0 * Math.Log10(lin + FloorLinear);
        }

        // fftshift: DC (bin 0) → index N/2, negative freqs left, positive right (PsdFrame contract).
        var shifted = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            shifted[i] = powerDb[(i + FftSize / 2) % FftSize];

        return new PsdFrame(block.CenterFreqHz, block.SampleRateHz, shifted);
    }

    public FeatureVector ExtractFeatures(PsdFrame frame, int durationMs)
    {
        double[] db = frame.PowerDbfs;
        int n = db.Length;
        double binHz = frame.BinWidthHz;

        int peak = 0;
        for (int k = 1; k < n; k++)
            if (db[k] > db[peak]) peak = k;

        double peakDb = db[peak];
        long centerFreq = frame.CenterFreqHz + (long)Math.Round((peak - n / 2.0) * binHz);

        double noiseFloor = NoiseFloor.Median(db);
        double snr = peakDb - noiseFloor;

        int bw3 = ExtentWidthHz(db, peakDb - 3.0, binHz);
        int bw20 = ExtentWidthHz(db, peakDb - 20.0, binHz);

        double duty = new OccupancyCalculator(noiseFloor + OccupancyMarginDb).Fraction(db);
        string hint = bw3 <= 2 * binHz ? "cw" : "wideband";

        return new FeatureVector(centerFreq, bw3, bw20, peakDb, snr, durationMs, duty, hint);
    }

    // Occupied bandwidth (Hz): span between the outermost bins whose power is ≥ thresholdDb
    // (the conventional edge-to-edge -N dB definition; tolerates in-band ripple).
    private static int ExtentWidthHz(double[] db, double thresholdDb, double binHz)
    {
        int lo = -1, hi = -1;
        for (int k = 0; k < db.Length; k++)
        {
            if (db[k] < thresholdDb) continue;
            if (lo < 0) lo = k;
            hi = k;
        }
        if (lo < 0) return 0;
        return (int)Math.Round((hi - lo + 1) * binHz);
    }
}
