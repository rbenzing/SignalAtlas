using System.Globalization;
using SignalAtlas.Domain;

namespace SignalAtlas.Fingerprint;

/// <summary>
/// Flags identity spoofing/cloning (SPEC §8.10, M10). When the <b>same</b> decoded identifier
/// (BSSID, ICAO, MAC, …) presents a fingerprint that does <b>not</b> match the known one, the ID has
/// been cloned onto different hardware. Surfaced as an <see cref="Alert"/> of kind
/// <see cref="SpoofAlertKind"/> with non-empty evidence (P4). Reuses the existing <see cref="Alert"/>
/// record and never touches the anomaly engine (the kind is a local const here).
/// <para>Reference-gated: at a worse <see cref="RefQuality"/> intra-device variance widens, so the
/// match threshold must be recalibrated per reference in the field (§18.3, G21).</para>
/// </summary>
public sealed class SpoofDetector
{
    /// <summary>Alert kind emitted on an ID/fingerprint mismatch (local const — does not touch Anomaly).</summary>
    public const string SpoofAlertKind = "fingerprint_spoof";

    private readonly FingerprintMatcher _matcher;

    public SpoofDetector() : this(new FingerprintMatcher()) { }

    public SpoofDetector(FingerprintMatcher matcher) =>
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));

    /// <summary>
    /// Returns a <c>fingerprint_spoof</c> <see cref="Alert"/> when the same decoded identifier
    /// presents a fingerprint that does not match the known hardware; otherwise <c>null</c>.
    /// </summary>
    public Alert? Check(string decodedIdentifier, DeviceFingerprint knownFingerprint, DeviceFingerprint newFingerprint)
    {
        ArgumentNullException.ThrowIfNull(decodedIdentifier);
        ArgumentNullException.ThrowIfNull(knownFingerprint);
        ArgumentNullException.ThrowIfNull(newFingerprint);

        double similarity = _matcher.Similarity(knownFingerprint, newFingerprint);
        if (similarity >= _matcher.Threshold)
            return null; // fingerprints match → same hardware presenting its own ID → not a spoof.

        // Same decoded ID, mismatched hardware → identity cloned. Deterministic Id (§6.1 P5).
        var id = DeterministicGuid.From(
            $"{SpoofAlertKind}:{decodedIdentifier}:{newFingerprint.EmitterId}:{newFingerprint.UpdatedAt:o}");

        var evidence = new List<EvidenceItem>
        {
            new("decoded_identifier", decodedIdentifier, 1.0),
            new("fingerprint_similarity", similarity.ToString("0.###", CultureInfo.InvariantCulture), 1.0),
            new("match_threshold", _matcher.Threshold.ToString("0.###", CultureInfo.InvariantCulture), 1.0),
            new("known_emitter_id", knownFingerprint.EmitterId, 0.5),
            new("known_ref_quality", knownFingerprint.RefQuality, 0.5),
            new("new_ref_quality", newFingerprint.RefQuality, 0.5),
        };

        return new Alert(
            Id: id,
            Time: newFingerprint.UpdatedAt,
            EmitterId: newFingerprint.EmitterId,
            DeviceId: null,
            Kind: SpoofAlertKind,
            Severity: "critical",
            Summary: $"Decoded identifier '{decodedIdentifier}' presents a fingerprint that does not "
                     + $"match the known hardware (similarity {similarity:0.###} < {_matcher.Threshold:0.###}) — possible spoof/clone.",
            Evidence: evidence);
    }
}
