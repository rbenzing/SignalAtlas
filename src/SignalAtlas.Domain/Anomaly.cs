namespace SignalAtlas.Domain;

/// <summary>An anomaly alert (SPEC §7.2 alerts, §8.8). Evidence non-empty (P4).</summary>
public sealed record Alert(
    Guid Id,
    DateTimeOffset Time,
    string? EmitterId,
    string? DeviceId,
    string Kind,
    string Severity,
    string Summary,
    IReadOnlyList<EvidenceItem> Evidence,
    bool Acknowledged = false)
{
    public const string NewEmitter = "new_emitter";
    public const string NewDevice = "new_device";
    public const string ProtocolChange = "protocol_change";
    public const string LocationChange = "location_change";
    public const string PowerChange = "power_change";
    public const string OccupancySpike = "occupancy_spike";
    public const string PredictedAnomaly = "predicted_anomaly";
}

/// <summary>An incoming event to test for anomalies (SPEC §8.8). Power is relative dBFS (G20).</summary>
public sealed record AnomalyEvent(
    DateTimeOffset Time,
    string EmitterId,
    string? DeviceId,
    string Protocol,
    double PowerDbfs,
    double? Latitude,
    double? Longitude,
    double Occupancy);

/// <summary>Prior known state for the emitter, or the "not yet seen" baseline (SPEC §8.8).</summary>
public sealed record EmitterBaseline(
    bool KnownEmitter,
    bool KnownDevice,
    string? LastProtocol,
    double? LastLatitude,
    double? LastLongitude,
    double? BaselinePowerDbfs,
    double? BaselineOccupancy);

/// <summary>
/// Rule-based anomaly detection (SPEC §8.8): new_emitter, new_device, protocol_change,
/// location_change, power_change (relative), occupancy_spike — with severity, non-empty
/// evidence, and deduplication (no duplicate alert for an already-known condition).
/// </summary>
public interface IAnomalyEngine
{
    IReadOnlyList<Alert> Evaluate(AnomalyEvent evt, EmitterBaseline baseline);
}
