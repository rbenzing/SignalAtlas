using SignalAtlas.Domain;
using SignalAtlas.Ml;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// A controllable hardware signature for synthesizing IQ (M10 tests, SPEC §8.10). Each field is a
/// PHY imperfection the transmitter hardware imprints on its baseband signal.
/// </summary>
public sealed record HardwareSignature(
    double GainImbalance, // log gain ratio I/Q (~0 = balanced)
    double PhaseErrRad,   // quadrature (I/Q phase) error, radians
    double DcI,           // DC offset on I (relative to unit amplitude)
    double DcQ,           // DC offset on Q
    double CfoHz,         // carrier frequency offset, Hz
    int RiseSamples,      // turn-on envelope rise length (samples)
    double Overshoot);    // turn-on envelope overshoot (relative)

/// <summary>
/// Deterministic synthetic IQ with an injected hardware signature (M10, SPEC §8.10, §6.1 P5). Uses
/// <see cref="DeterministicRng"/> so a given seed reproduces the exact block. The frequency reference
/// (<see cref="RefQuality"/>) scales per-capture jitter: a worse reference (crystal) widens the
/// repeatability of every imperfection, tighter references (tcxo/gpsdo) narrow it — the physical
/// basis of the reference-gated NFR-A5 ceiling (G21). <b>Synthetic: field accuracy is gated (§18.3).</b>
/// </summary>
public static class FingerprintSynth
{
    public const int DefaultSamples = 2048;
    public const int DefaultSampleRate = 2_000_000;
    public const double ToneFraction = 0.10; // baseband tone at 0.10 * Fs

    /// <summary>Per-capture jitter multiplier — worse reference ⇒ wider intra-device variance (G21).</summary>
    public static double RefJitter(string refQuality) => refQuality switch
    {
        RefQuality.Gpsdo => 0.12,
        RefQuality.Tcxo => 0.22,
        RefQuality.Crystal => 1.0,
        _ => 1.0,
    };

    public static IqBlock Synthesize(
        HardwareSignature hw,
        string refQuality,
        ulong seed,
        long centerFreqHz = 100_000_000,
        int n = DefaultSamples,
        int sampleRate = DefaultSampleRate,
        double toneFraction = ToneFraction)
    {
        var rng = new DeterministicRng(seed);
        double jitter = RefJitter(refQuality);

        // Per-capture realization of the (fixed) hardware imperfections — the reference gates spread.
        double gain = hw.GainImbalance + rng.NextGaussian(0, 0.05 * jitter);
        double phase = hw.PhaseErrRad + rng.NextGaussian(0, 0.05 * jitter);
        double dcI = hw.DcI + rng.NextGaussian(0, 0.02 * jitter);
        double dcQ = hw.DcQ + rng.NextGaussian(0, 0.02 * jitter);
        double cfoHz = hw.CfoHz + rng.NextGaussian(0, 3000.0 * jitter);
        double rise = Math.Max(5.0, hw.RiseSamples * (1.0 + rng.NextGaussian(0, 0.5 * jitter)));
        double overshoot = Math.Max(0.0, hw.Overshoot + rng.NextGaussian(0, 0.03 * jitter));

        double f0 = toneFraction * sampleRate;
        double w = 2.0 * Math.PI * (f0 + cfoHz) / sampleRate; // rad/sample
        double gI = 1.0 + gain / 2.0;
        double gQ = 1.0 - gain / 2.0;
        const double noiseStd = 0.008;
        double pnStd = 0.02 * jitter; // phase-noise random-walk increment std

        var i = new float[n];
        var q = new float[n];
        double theta = 0.0;
        double pnWalk = 0.0;
        for (int s = 0; s < n; s++)
        {
            double env = Envelope(s, rise, overshoot);
            double ph = theta + pnWalk;
            double ci = env * Math.Cos(ph);
            double cq = env * Math.Sin(ph);

            // quadrature (phase) error skews Q into I; gain imbalance scales the arms.
            double iOut = gI * ci;
            double qOut = gQ * (cq * Math.Cos(phase) + ci * Math.Sin(phase));

            iOut += dcI + rng.NextGaussian(0, noiseStd);
            qOut += dcQ + rng.NextGaussian(0, noiseStd);

            i[s] = (float)iOut;
            q[s] = (float)qOut;

            theta += w;
            pnWalk += rng.NextGaussian(0, pnStd);
        }

        return new IqBlock(centerFreqHz, sampleRate, i, q);
    }

    private static double Envelope(int s, double rise, double overshoot)
    {
        double x = s / rise;
        double baseRise = 1.0 - Math.Exp(-5.0 * x);   // ~1 in steady state
        double bump = overshoot * x * Math.Exp(1.0 - x); // peaks ~overshoot near x=1
        return baseRise + bump;
    }
}
