namespace SignalAtlas.Domain;

/// <summary>A position fix from an <see cref="IPositionSource"/>.</summary>
public sealed record Position(
    double Latitude,
    double Longitude,
    double? AltitudeM,
    PositionQuality Quality);
