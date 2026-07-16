using SignalAtlas.Domain;

namespace SignalAtlas.Fingerprint;

/// <summary>
/// Compares two <see cref="DeviceFingerprint"/> embeddings (SPEC §8.10, M10).
/// <para>
/// <see cref="Similarity"/> is the <b>cosine similarity of the standardized embeddings</b>, mapped
/// from [-1,1] onto [0,1]. Standardization z-scores each dimension with fixed per-feature mean/scale
/// constants (<see cref="Mu"/>/<see cref="Sigma"/>) so no dimension's raw units dominate the angle;
/// CFO is scaled loosely so a different carrier does not sink a same-hardware match (drift-robust,
/// §8.10). Two captures of the same hardware point the same direction → similarity ≈ 1; different
/// hardware points elsewhere → similarity ≈ 0.5 or below.
/// </para>
/// <para>
/// <b>Reference gating (G21):</b> a worse <see cref="RefQuality"/> widens intra-device variance, so
/// the same threshold yields a lower true-match rate. <see cref="DefaultSameHardwareThreshold"/> is a
/// <b>synthetic</b> operating point (§18.3): field deployment must recalibrate it per <c>ref_quality</c>
/// and NFR-A5 (≥90% true-match @ ≤5% false-match) stays reference-gated and field-validated.
/// </para>
/// </summary>
public sealed class FingerprintMatcher
{
    /// <summary>
    /// Default "same hardware" decision threshold on <see cref="Similarity"/>. Synthetic operating
    /// point tuned for a good reference; NOT a field guarantee (§18.3, reference-gated NFR-A5).
    /// </summary>
    public const double DefaultSameHardwareThreshold = 0.90;

    // Per-feature standardization constants (synthetic population; field must recalibrate, §18.3).
    // Order matches PhyFingerprinter's Idx* layout. CFO (idx 4) is scaled loosely on purpose so a
    // carrier change does not dominate the angle — the hardware signature lives in the other dims.
    private static readonly double[] Mu = { 0.0, 0.0, 0.0, 0.0, 0.10, 0.05, 0.03, 0.02 };
    private static readonly double[] Sigma = { 0.05, 0.05, 0.02, 0.02, 0.06, 0.03, 0.03, 0.015 };

    private readonly double _threshold;

    public FingerprintMatcher() : this(DefaultSameHardwareThreshold) { }

    public FingerprintMatcher(double sameHardwareThreshold) => _threshold = sameHardwareThreshold;

    public double Threshold => _threshold;

    /// <summary>Cosine similarity of the standardized embeddings, mapped to [0,1] (1 = identical hw).</summary>
    public double Similarity(DeviceFingerprint a, DeviceFingerprint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Similarity(a.FeatureVector, b.FeatureVector);
    }

    public static double Similarity(double[] a, double[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Embeddings must have equal length.");

        double dot = 0, na = 0, nb = 0;
        for (int k = 0; k < a.Length; k++)
        {
            double za = (a[k] - Mu[k]) / Sigma[k];
            double zb = (b[k] - Mu[k]) / Sigma[k];
            dot += za * zb;
            na += za * za;
            nb += zb * zb;
        }

        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        double cos = denom > 1e-12 ? dot / denom : 0.0;
        double sim = (cos + 1.0) * 0.5;
        return Math.Clamp(sim, 0.0, 1.0);
    }

    /// <summary>True when the two fingerprints are the same hardware at the given threshold.</summary>
    public bool IsSameHardware(DeviceFingerprint a, DeviceFingerprint b) =>
        Similarity(a, b) >= _threshold;

    public bool IsSameHardware(DeviceFingerprint a, DeviceFingerprint b, double threshold) =>
        Similarity(a, b) >= threshold;
}
