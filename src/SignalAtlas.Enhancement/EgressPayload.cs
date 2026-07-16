using System.Text.Json;
using SignalAtlas.Domain;

namespace SignalAtlas.Enhancement;

/// <summary>
/// A signal reduced to STRUCTURED RF metadata for egress (SPEC §4.2 L7). By construction it carries no
/// IQ samples / raw-sample buffers — only frequency/bandwidth/duration + the classifier's confidence,
/// evidence and (filtered) feature scalars. Feature keys that look like raw IQ / cleartext payload are
/// stripped by <see cref="SessionIntelligence"/> before this is built (AC-DA3).
/// </summary>
public sealed record EgressSignal(
    long Id,
    string Protocol,
    double Confidence,
    long CenterFreqHz,
    int BandwidthHz,
    int? DurationMs,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyDictionary<string, double> Features);

/// <summary>An emitter reduced to structured RF metadata + decoded identifiers for egress (§4.2 L7).</summary>
public sealed record EgressEmitter(
    string Id,
    string? DeviceId,
    string Protocol,
    long FreqCenterHz,
    double? EstLatitude,
    double? EstLongitude,
    double? EstUncertaintyM,
    IReadOnlyDictionary<string, string> Identifiers,
    IReadOnlyList<EvidenceItem> Evidence);

/// <summary>
/// A decoded frame reduced to cleartext identity/control metadata only (SPEC §4.2 L2/L3). Encrypted /
/// user payload is never present in the domain record, so it can never reach egress.
/// </summary>
public sealed record EgressDecodedFrame(
    string Protocol,
    string FrameType,
    IReadOnlyDictionary<string, string> Identifiers,
    double DecodeQuality);

/// <summary>
/// The structured egress payload handed to Claude (SPEC §4.2 L7, §8.13). It contains ONLY structured RF
/// intelligence (signals/features/decoded-frames/emitters/evidence) and, by TYPE construction, has no
/// member for raw IQ samples or cleartext personal content (AC-DA3). <see cref="EgressGuard"/> re-asserts
/// this at runtime.
/// </summary>
public sealed record EgressPayload(
    string? SessionId,
    IReadOnlyList<EgressSignal> Signals,
    IReadOnlyList<EgressEmitter> Emitters,
    IReadOnlyList<EgressDecodedFrame> DecodedFrames);

/// <summary>
/// Controlled-egress guard (SPEC §4.2 L7, AC-DA3): asserts a payload bound for the Claude API contains
/// only structured RF metadata — never raw IQ samples and never cleartext personal content. The
/// <see cref="EgressPayload"/> type already excludes those by construction; this scans the serialized
/// form for forbidden markers as belt-and-suspenders and is also used to filter feature keys upstream.
/// </summary>
public static class EgressGuard
{
    // Markers that must NEVER cross the network boundary (§4.2 L7 raw IQ, L3 cleartext user payload).
    // Structured RF metadata — center_freq, bandwidth, snr, decoded IDs — is explicitly allowed.
    private static readonly string[] Forbidden =
        ["raw_iq", "iq_sample", "iq_blob", "iqref", "iq_ref", "cleartext", "payload_bytes", "raw_samples"];

    /// <summary>True if a feature/identifier key names raw-IQ or cleartext payload (must be dropped).</summary>
    public static bool IsForbiddenKey(string key) =>
        Forbidden.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>Throws if the serialized payload contains any forbidden raw-IQ / cleartext marker (AC-DA3).</summary>
    public static void AssertNoRawIqOrContent(EgressPayload payload)
    {
        var json = JsonSerializer.Serialize(payload);
        foreach (var marker in Forbidden)
            if (json.Contains(marker, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Egress payload contains forbidden content marker '{marker}' (SPEC §4.2 L7 controlled egress).");
    }
}
