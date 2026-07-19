using SignalAtlas.Domain;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M1 Signal Processing Engine tests (SPEC §8.2). Deterministic synthetic input (P5).
/// Tolerances chosen: Hann scalloping loss 1.42 dB theoretical ±0.4 dB; BW ±10% (AC-P3).
/// </summary>
public class ProcessingTests
{
    private const int Fs = 1_000_000; // 1 MHz sample rate
    private const int N = 4096;       // FFT points (SPEC §8.2)
    private const double BinHz = (double)Fs / N;

    // Complex tone exp(j 2π f n / fs), amplitude a, over `samples` points.
    private static IqBlock Tone(double freqHz, double a, int samples, long centerHz = 100_000_000)
    {
        var i = new float[samples];
        var q = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            double ph = 2.0 * Math.PI * freqHz * n / Fs;
            i[n] = (float)(a * Math.Cos(ph));
            q[n] = (float)(a * Math.Sin(ph));
        }
        return new IqBlock(centerHz, Fs, i, q);
    }

    // AC-P1: pure sinusoid at a known bin → PSD peak within ±1 bin of expected.
    [Fact]
    public void Fft_GoldenSinusoid_PeaksAtExpectedBin()
    {
        int offsetBins = 100;                       // +100 bins from center
        var block = Tone(offsetBins * BinHz, 1.0, 8192);

        var psd = new SignalProcessor().ComputePsd(block);

        int peak = ArgMax(psd.PowerDbfs);
        int expected = N / 2 + offsetBins;          // DC at N/2, positive freqs to the right
        Assert.InRange(peak, expected - 1, expected + 1);
    }

    // Hann scalloping: worst-case bin-center vs bin-edge amplitude loss ≈ 1.42 dB (documented).
    [Fact]
    public void Hann_ScallopingLoss_WithinTolerance()
    {
        var center = new SignalProcessor().ComputePsd(Tone(100 * BinHz, 1.0, 8192));
        var edge = new SignalProcessor().ComputePsd(Tone((100 + 0.5) * BinHz, 1.0, 8192));

        double loss = center.PowerDbfs.Max() - edge.PowerDbfs.Max();
        Assert.InRange(loss, 1.0, 1.9); // Hann theoretical 1.42 dB
    }

    // AC-P2 / §6.3: occupancy 0.0 / 0.5 / 1.0.
    [Fact]
    public void Occupancy_AllBelow_IsZero()
        => Assert.Equal(0.0, new OccupancyCalculator(-90).Fraction(new[] { -100.0, -98, -101 }), 3);

    [Fact]
    public void Occupancy_HalfAbove_IsHalf()
        => Assert.Equal(0.5, new OccupancyCalculator(-90).Fraction(new[] { -100.0, -50, -101, -40 }), 3);

    [Fact]
    public void Occupancy_AllAbove_IsOne()
        => Assert.Equal(1.0, new OccupancyCalculator(-90).Fraction(new[] { -50.0, -40, -30 }), 3);

    // AC-P4: median noise floor unmoved by a single large burst (spike).
    [Fact]
    public void NoiseFloor_Median_RobustToSingleBurst()
    {
        var quiet = new double[512];
        for (int k = 0; k < quiet.Length; k++) quiet[k] = -100.0 + (k % 3);   // ~-100 dB noise
        double baseline = NoiseFloor.Median(quiet);

        var withBurst = (double[])quiet.Clone();
        withBurst[256] = 0.0;                                                  // one huge spike
        double spiked = NoiseFloor.Median(withBurst);

        Assert.Equal(baseline, spiked, 3);
    }

    // AC-P3: -3 dB bandwidth of a synthesized 125 kHz flat band within ±10%.
    [Fact]
    public void Bandwidth3dB_OfFlatBand_Within10Percent()
    {
        const double bw = 125_000.0;
        var block = FlatBand(bw, samples: 16384);

        var psd = new SignalProcessor().ComputePsd(block);
        var f = new SignalProcessor().ExtractFeatures(psd, durationMs: 10);

        Assert.InRange(f.Bandwidth3dBHz, bw * 0.9, bw * 1.1);
    }

    // Regression for the Welch scratch-buffer hoist (#11): a block shorter than one FFT segment
    // forces the padded first-and-only segment path (idx >= count for n near the end). The reused
    // re/im buffers must have their pad tail explicitly zeroed, matching pre-hoist fresh-array
    // behavior byte-for-byte.
    [Fact]
    public void ShortBlock_PaddedSegment_PeaksAtExpectedBin()
    {
        int offsetBins = 50;
        var block = Tone(offsetBins * BinHz, 1.0, samples: 2048); // < FftSize -> padded segment

        var psd = new SignalProcessor().ComputePsd(block);

        int peak = ArgMax(psd.PowerDbfs);
        int expected = N / 2 + offsetBins;
        Assert.InRange(peak, expected - 1, expected + 1);
    }

    // Equal-amplitude tones on every bin across `bandwidthHz` (Schroeder phases, deterministic) →
    // a flat occupied band whose -3 dB width equals the band. Avoids chirp ripple (P5).
    private static IqBlock FlatBand(double bandwidthHz, int samples)
    {
        int half = (int)Math.Round(bandwidthHz / 2.0 / BinHz);
        int tones = 2 * half + 1;
        var i = new float[samples];
        var q = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            double sr = 0, si = 0;
            for (int k = -half; k <= half; k++)
            {
                double phase0 = Math.PI * k * k / tones;   // Schroeder: low peak-to-average
                double ph = 2.0 * Math.PI * (k * BinHz) * n / Fs + phase0;
                sr += Math.Cos(ph);
                si += Math.Sin(ph);
            }
            i[n] = (float)(sr / tones);
            q[n] = (float)(si / tones);
        }
        return new IqBlock(100_000_000, Fs, i, q);
    }

    private static int ArgMax(double[] a)
    {
        int idx = 0;
        for (int k = 1; k < a.Length; k++)
            if (a[k] > a[idx]) idx = k;
        return idx;
    }
}
