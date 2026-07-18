namespace SignalAtlas.Domain;

/// <summary>
/// A demodulated, CRC-validated frame carrying only identity/control-plane metadata
/// (SPEC §4.2 L2, §7.2 decoded_frames). Encrypted/user payload is never present (L3).
/// </summary>
public sealed record DecodedFrame(
    string Protocol,
    string FrameType,
    IReadOnlyDictionary<string, string> Identifiers,
    double DecodeQuality,
    IReadOnlyList<EvidenceItem> Evidence,
    CprPosition? Cpr = null);

/// <summary>
/// Raw airborne-position payload from an ADS-B extended squitter (TC 9-18): the CPR format bit,
/// the two 17-bit compact-position values, and barometric altitude (ft). Carried on the frame for
/// the position resolver to consume — NEVER merged into a Device's identifiers/evidence (these
/// values change every frame). Null for non-position frames.
/// </summary>
public sealed record CprPosition(bool Odd, int CprLat17, int CprLon17, int AltitudeFt);

/// <summary>Outcome of a decode attempt. A frame is produced only when the CRC/FCS passes (AC-D6).</summary>
public sealed record DecodeOutcome(bool Success, DecodedFrame? Frame, string? RejectReason)
{
    public static DecodeOutcome Decoded(DecodedFrame frame) => new(true, frame, null);
    public static DecodeOutcome Rejected(string reason) => new(false, null, reason);
}

/// <summary>
/// A determined device (SPEC §7.2 devices). Built from decoded identity frames by an
/// <see cref="IDeviceResolver"/>. Vendor is set only when the MAC OUI is meaningful (LAA clear).
/// </summary>
public sealed record Device(
    string Id,
    string DeviceType,
    string? PrimaryIdentifier,
    IReadOnlyDictionary<string, string> Identifiers,
    string? Vendor,
    string Protocol,
    double Confidence,
    IReadOnlyList<EvidenceItem> Evidence,
    double? Latitude = null,
    double? Longitude = null,
    int? AltitudeFt = null);

/// <summary>
/// Decodes a single protocol's demodulated frame bytes into a <see cref="DecodedFrame"/>
/// (SPEC §8.4). CRC/FCS must pass; only cleartext identity/management metadata is parsed (L2/L3).
/// NOTE: the IQ→bytes demodulation front-end (per-protocol DSP) is a separate seam
/// (<see cref="IDemodulator"/>) and is deferred until real .iq fixtures exist; this contract
/// operates on the demodulated frame, matching SPEC §12.2 "craftable valid protocol frames".
/// </summary>
public interface IProtocolDecoder
{
    string Protocol { get; }
    bool CanDecode(string protocol);
    DecodeOutcome Decode(ReadOnlyMemory<byte> frameBytes);
}

/// <summary>IQ→frame-bytes demodulation seam (deferred — needs field .iq fixtures). SPEC §8.4 Tier A/B.</summary>
public interface IDemodulator
{
    string Protocol { get; }
    IEnumerable<ReadOnlyMemory<byte>> Demodulate(IqBlock slice, FeatureVector features);
}

/// <summary>Dispatches a frame to the decoder registered for its protocol; unknown → no-op (AC: registry no-op).</summary>
public interface IDecoderRegistry
{
    DecodeOutcome Decode(string protocol, ReadOnlyMemory<byte> frameBytes);
}

/// <summary>Determines a <see cref="Device"/> from decoded identity frames (SPEC §8.4 IDeviceResolver).</summary>
public interface IDeviceResolver
{
    Device? Resolve(IReadOnlyList<DecodedFrame> frames);
}

/// <summary>OUI→vendor lookup (SPEC §7.4). Returns null for locally-administered/randomized MACs (§8.4 LAA check).</summary>
public interface IOuiLookup
{
    string? Vendor(string mac);
    bool IsLocallyAdministered(string mac);
}

/// <summary>A decoded geographic position (WGS-84 degrees). Aircraft self-reported location (L2).</summary>
public sealed record GeoPosition(double Latitude, double Longitude);

/// <summary>
/// Resolves ADS-B airborne CPR frames into an absolute position via global (even/odd) decoding.
/// Stateful: caches the last even + last odd frame per ICAO and returns a fix once it holds a
/// consistent pair within the pairing window. Deterministic — position depends only on the frame
/// values and the caller-supplied timestamps (IClock-sourced), never a wall clock.
/// </summary>
public interface ICprPositionResolver
{
    GeoPosition? Accept(string icao, bool odd, int cprLat17, int cprLon17, DateTimeOffset time);
}
