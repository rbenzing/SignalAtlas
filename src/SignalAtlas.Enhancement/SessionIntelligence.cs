using SignalAtlas.Domain;

namespace SignalAtlas.Enhancement;

/// <summary>
/// The deterministic tool layer (SPEC §8.13) that assembles a session's already-complete edge
/// intelligence into a structured <see cref="EgressPayload"/> — signals/features/decoded-frames/
/// emitters/evidence. NO Claude here; unit-tested in isolation. Enforces the egress guard (§4.2 L7,
/// AC-DA3): raw-IQ references are dropped and feature keys naming raw IQ / cleartext are filtered out,
/// so only structured RF metadata leaves the boundary.
/// </summary>
public static class SessionIntelligence
{
    /// <summary>
    /// Assembles the structured egress payload from the session's edge records. Raw IQ never enters:
    /// <see cref="Observation.IqRef"/> (the pointer to retained IQ) is intentionally NOT copied, and
    /// any feature key that names raw IQ / cleartext payload is stripped (<see cref="EgressGuard"/>).
    /// </summary>
    public static EgressPayload Assemble(
        string? sessionId,
        IReadOnlyList<Signal> signals,
        IReadOnlyList<Emitter> emitters,
        IReadOnlyList<DecodedFrame> decodedFrames,
        IReadOnlyList<Observation> observations)
    {
        // observations are accepted so callers can pass the raw session slice; their IqRef / raw power
        // are deliberately excluded from egress (§4.2 L7). Referenced here only to make that explicit.
        _ = observations;

        var egressSignals = signals.Select(s => new EgressSignal(
            s.Id,
            s.Protocol,
            s.Confidence,
            s.CenterFreqHz,
            s.BandwidthHz,
            s.DurationMs,
            s.Evidence,
            FilterFeatures(s.Features))).ToList();

        var egressEmitters = emitters.Select(e => new EgressEmitter(
            e.Id,
            e.DeviceId,
            e.Protocol,
            e.FreqCenterHz,
            e.EstLatitude,
            e.EstLongitude,
            e.EstUncertaintyM,
            e.Identifiers,
            e.Evidence)).ToList();

        var egressFrames = decodedFrames.Select(f => new EgressDecodedFrame(
            f.Protocol,
            f.FrameType,
            f.Identifiers,
            f.DecodeQuality)).ToList();

        return new EgressPayload(sessionId, egressSignals, egressEmitters, egressFrames);
    }

    private static IReadOnlyDictionary<string, double> FilterFeatures(IReadOnlyDictionary<string, double> features)
        => features
            .Where(kv => !EgressGuard.IsForbiddenKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
}
