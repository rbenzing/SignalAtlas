namespace SignalAtlas.Domain;

/// <summary>
/// A hardware RF/PHY fingerprint for an emitter (SPEC §7.2 <c>device_fingerprints</c>, §8.10, M10).
/// <para>
/// <see cref="FeatureVector"/> is a fixed-length, drift-robust embedding of PHY imperfections
/// (I/Q gain imbalance, quadrature error, DC offset, carrier-frequency-offset residual, turn-on
/// transient shape, phase-noise proxy) that identify the transmitter <b>hardware</b>, not its
/// carrier or gain (§8.10). <see cref="Stability"/> ∈ [0,1] and the achievable separation are
/// <b>reference-gated</b> (G21): a worse frequency reference widens intra-device variance, so
/// <see cref="RefQuality"/> is stored alongside the embedding and callers must scale their
/// expectations by it. <b>Lab numbers ≠ field</b> (§18.3): NFR-A5 (≥90% true-match @ ≤5%
/// false-match) is reference-gated and field-validated, never assumed from synthetic accuracy.
/// </para>
/// </summary>
public sealed record DeviceFingerprint(
    string EmitterId,
    double[] FeatureVector,
    double Stability,
    string RefQuality,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Frequency-reference quality that gates fingerprint precision (SPEC §4.5, G21). A HackRF's stock
/// ~20 ppm crystal drifts, bounding CFO/phase-noise features; a TCXO or GPS-disciplined reference
/// tightens them. Worse reference → lower achievable precision → wider intra-device variance.
/// </summary>
public static class RefQuality
{
    /// <summary>Stock ~20 ppm crystal oscillator — the widest drift, the lowest ceiling (§4.5).</summary>
    public const string Crystal = "crystal";

    /// <summary>Temperature-compensated crystal oscillator — tighter than a plain crystal.</summary>
    public const string Tcxo = "tcxo";

    /// <summary>GPS-disciplined oscillator (1PPS) — the tightest reference, the best ceiling.</summary>
    public const string Gpsdo = "gpsdo";

    /// <summary>All recognised reference qualities, worst → best.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Crystal, Tcxo, Gpsdo };

    public static bool IsValid(string refQuality) =>
        refQuality is Crystal or Tcxo or Gpsdo;
}

/// <summary>
/// Extracts a <see cref="DeviceFingerprint"/> from a slice of raw IQ (SPEC §8.10, M10). The
/// embedding is fully deterministic (§6.1 P5): identical IQ + <paramref name="refQuality"/> +
/// <paramref name="emitterId"/> → an identical <see cref="DeviceFingerprint.FeatureVector"/>.
/// </summary>
public interface IFingerprinter
{
    DeviceFingerprint Extract(IqBlock slice, string refQuality, string emitterId);
}
