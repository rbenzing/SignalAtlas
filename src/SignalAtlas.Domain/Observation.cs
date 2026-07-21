namespace SignalAtlas.Domain;

/// <summary>
/// A raw RF measurement emitted by the Collector (SPEC §7.2 observations table).
/// Power is relative (dBFS) by default with <see cref="PowerRef"/> (G20); position may be
/// null with <see cref="PositionQuality.None"/> under GPS denial (G13).
/// </summary>
public sealed record Observation(
    DateTimeOffset Time,
    TimeSource TimeSource,
    string CollectorId,
    long Seq,
    long FrequencyHz,
    int BandwidthHz,
    double Power,
    PowerRef PowerRef,
    double? SnrDb,
    double? Latitude,
    double? Longitude,
    PositionQuality PositionQuality,
    string? IqRef,
    Guid CorrelationId,
    ReceiverConfig? ReceiverConfig = null);
