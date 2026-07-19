using SignalAtlas.Domain;

namespace SignalAtlas.Fingerprint;

/// <summary>
/// Extracts a drift-robust PHY-imperfection embedding from raw IQ (SPEC §8.10, M10, G18/G21).
/// <para>
/// The features identify the transmitter <b>hardware</b>, not its carrier or gain: they are
/// relative/ratio quantities (§8.10) so an emitter re-ID's across rotating IDs and different
/// carriers. The 8-dim embedding is <see cref="EmbeddingLength"/> long and fully deterministic
/// (§6.1 P5). CFO and phase-noise features are the ones the frequency reference gates (G21);
/// <see cref="DeviceFingerprint.Stability"/> is derived from <see cref="RefQuality"/> so callers
/// scale their expectations by the reference. <b>Lab ≠ field (§18.3): NFR-A5 is reference-gated.</b>
/// </para>
/// </summary>
public sealed class PhyFingerprinter : IFingerprinter
{
    /// <summary>Fixed embedding length (§8.10 feature set).</summary>
    public const int EmbeddingLength = 8;

    // Embedding layout — each dimension is a distinct PHY imperfection (§8.10).
    public const int IdxGainImbalance = 0;   // log(rmsI/rmsQ) — I/Q gain imbalance (gain-robust ratio)
    public const int IdxQuadratureError = 1; // asin(corr(I,Q)) — I/Q phase (quadrature) error, rad
    public const int IdxDcI = 2;             // mean(I)/rms — DC offset on I (gain-robust)
    public const int IdxDcQ = 3;             // mean(Q)/rms — DC offset on Q (gain-robust)
    public const int IdxCfo = 4;             // fractional carrier-frequency-offset residual (ref-gated)
    public const int IdxTransientRise = 5;   // turn-on envelope rise time (fraction of block)
    public const int IdxTransientOvershoot = 6; // turn-on envelope overshoot (relative)
    public const int IdxPhaseNoise = 7;      // std of instantaneous frequency — phase-noise proxy (ref-gated)

    private readonly IClock _clock;

    /// <summary>Requires an injected clock — deterministic core (§6.1 P5) never reads the wall clock directly.</summary>
    public PhyFingerprinter(IClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public DeviceFingerprint Extract(IqBlock slice, string refQuality, string emitterId)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(emitterId);

        int n = slice.SampleCount;
        var iSamples = slice.I;
        var qSamples = slice.Q;
        var embedding = new double[EmbeddingLength];

        if (n < 4)
        {
            // Too short to characterize — return a zero embedding (deterministic, still valid).
            return new DeviceFingerprint(emitterId, embedding, StabilityFor(refQuality), refQuality, _clock.UtcNow);
        }

        // --- DC offset (mean of each arm) ---
        double sumI = 0, sumQ = 0;
        for (int k = 0; k < n; k++) { sumI += iSamples[k]; sumQ += qSamples[k]; }
        double meanI = sumI / n, meanQ = sumQ / n;

        // --- AC power + I/Q cross-correlation (gain imbalance + quadrature error) ---
        double sii = 0, sqq = 0, siq = 0;
        for (int k = 0; k < n; k++)
        {
            double di = iSamples[k] - meanI;
            double dq = qSamples[k] - meanQ;
            sii += di * di;
            sqq += dq * dq;
            siq += di * dq;
        }
        double rmsI = Math.Sqrt(sii / n);
        double rmsQ = Math.Sqrt(sqq / n);
        double amp = Math.Sqrt(rmsI * rmsI + rmsQ * rmsQ);

        double gainImbalance = (rmsI > 1e-9 && rmsQ > 1e-9) ? Math.Log(rmsI / rmsQ) : 0.0;
        double corr = (rmsI > 1e-9 && rmsQ > 1e-9) ? (siq / n) / (rmsI * rmsQ) : 0.0;
        double quadError = Math.Asin(Math.Clamp(corr, -0.999, 0.999));
        double dcI = amp > 1e-9 ? meanI / amp : 0.0;
        double dcQ = amp > 1e-9 ? meanQ / amp : 0.0;

        // --- Carrier-frequency-offset residual + phase-noise proxy ---
        // z[k]*conj(z[k-1]) → its mean angle is the dominant tone (cycles/sample); the spread of the
        // per-sample angle about that mean is a phase-noise proxy (the ref-gated features, G21).
        double sumRe = 0, sumIm = 0;
        var instAngle = new double[n - 1];
        for (int k = 1; k < n; k++)
        {
            double re = iSamples[k] * iSamples[k - 1] + qSamples[k] * qSamples[k - 1];
            double im = qSamples[k] * iSamples[k - 1] - iSamples[k] * qSamples[k - 1];
            sumRe += re;
            sumIm += im;
            instAngle[k - 1] = Math.Atan2(im, re);
        }
        double meanAngle = Math.Atan2(sumIm, sumRe);
        double cfo = meanAngle / (2.0 * Math.PI); // fractional frequency (cycles/sample)

        double sacc = 0;
        for (int k = 0; k < instAngle.Length; k++)
        {
            double d = instAngle[k] - meanAngle;
            while (d > Math.PI) d -= 2.0 * Math.PI;
            while (d < -Math.PI) d += 2.0 * Math.PI;
            sacc += d * d;
        }
        double phaseNoise = Math.Sqrt(sacc / instAngle.Length);

        // --- Turn-on transient (envelope rise + overshoot), smoothed to reject noise spikes ---
        var env = new double[n];
        for (int k = 0; k < n; k++)
            env[k] = Math.Sqrt(iSamples[k] * iSamples[k] + qSamples[k] * qSamples[k]);
        var envSmooth = BoxSmooth(env, 16);

        double steady = MedianOfLastHalf(envSmooth);
        double riseFrac = 0.0, overshoot = 0.0;
        if (steady > 1e-9)
        {
            int riseIdx = n - 1;
            double gate = 0.9 * steady;
            for (int k = 0; k < n; k++) { if (envSmooth[k] >= gate) { riseIdx = k; break; } }
            riseFrac = (double)riseIdx / n;

            double max = 0.0;
            for (int k = 0; k < n; k++) if (envSmooth[k] > max) max = envSmooth[k];
            overshoot = Math.Max(0.0, (max / steady) - 1.0);
        }

        embedding[IdxGainImbalance] = gainImbalance;
        embedding[IdxQuadratureError] = quadError;
        embedding[IdxDcI] = dcI;
        embedding[IdxDcQ] = dcQ;
        embedding[IdxCfo] = cfo;
        embedding[IdxTransientRise] = riseFrac;
        embedding[IdxTransientOvershoot] = overshoot;
        embedding[IdxPhaseNoise] = phaseNoise;

        return new DeviceFingerprint(emitterId, embedding, StabilityFor(refQuality), refQuality, _clock.UtcNow);
    }

    /// <summary>
    /// Achievable-stability prior from the frequency reference (G21): a worse reference bounds the
    /// repeatability of the CFO/phase-noise features, so <c>gpsdo &gt; tcxo &gt; crystal</c>. This is
    /// a synthetic prior — the field ceiling is measured, not assumed (§18.3).
    /// </summary>
    private static double StabilityFor(string refQuality) => refQuality switch
    {
        RefQuality.Gpsdo => 0.95,
        RefQuality.Tcxo => 0.80,
        RefQuality.Crystal => 0.55,
        _ => 0.55,
    };

    private static double[] BoxSmooth(double[] x, int window)
    {
        int n = x.Length;
        var outp = new double[n];
        if (window <= 1) { Array.Copy(x, outp, n); return outp; }
        double sum = 0;
        for (int k = 0; k < n; k++)
        {
            sum += x[k];
            if (k >= window) sum -= x[k - window];
            int count = Math.Min(k + 1, window);
            outp[k] = sum / count;
        }
        return outp;
    }

    private static double MedianOfLastHalf(double[] x)
    {
        int start = x.Length / 2;
        int len = x.Length - start;
        var slice = new double[len];
        Array.Copy(x, start, slice, 0, len);
        Array.Sort(slice);
        return len % 2 == 1 ? slice[len / 2] : 0.5 * (slice[len / 2 - 1] + slice[len / 2]);
    }
}
