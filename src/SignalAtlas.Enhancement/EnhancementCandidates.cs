using SignalAtlas.Domain;

namespace SignalAtlas.Enhancement;

/// <summary>
/// The per-session enhancement-candidate count (SPEC §8.13, AC-DA7). Broken down into the residual hard
/// cases the Claude pass would target: <see cref="LowConfidenceSignals"/> (Unknown or below the
/// confidence floor) and <see cref="UnresolvedDevices"/> (emitters not yet resolved to a device).
/// <see cref="Total"/> is what the operator weighs for "ML resolved 96% confidently — skip" vs
/// "lots of unknowns — enhance."
/// </summary>
public sealed record EnhancementCandidateReport(int LowConfidenceSignals, int UnresolvedDevices)
{
    public int Total => LowConfidenceSignals + UnresolvedDevices;
}

/// <summary>
/// Computes the enhancement-candidate count from EDGE DATA ALONE — deterministic, NO Claude (AC-DA7).
/// This informs the operator's run/skip decision (§4.10) before any egress or API call.
/// </summary>
public static class EnhancementCandidates
{
    /// <summary>
    /// Documented low-confidence floor (SPEC §8.13 "confidence &lt; a documented floor, e.g. 0.6"): a
    /// signal at or above this is treated as confidently classified; below it (or <c>Unknown</c>) it is
    /// a candidate for the enhancement pass.
    /// </summary>
    public const double ConfidenceFloor = 0.6;

    private const string UnknownProtocol = "Unknown";

    public static EnhancementCandidateReport Count(
        IReadOnlyList<Signal> signals,
        IReadOnlyList<Emitter> emitters)
    {
        int lowConfidence = signals.Count(s =>
            string.Equals(s.Protocol, UnknownProtocol, StringComparison.OrdinalIgnoreCase)
            || s.Confidence < ConfidenceFloor);

        // An emitter with no determined device is an "unresolved device" candidate (§8.13).
        int unresolvedDevices = emitters.Count(e => string.IsNullOrEmpty(e.DeviceId));

        return new EnhancementCandidateReport(lowConfidence, unresolvedDevices);
    }
}
