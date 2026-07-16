namespace SignalAtlas.Domain;

/// <summary>An observation with an optional position and its relative power (SPEC §8.6 input).</summary>
public sealed record PositionedObservation(double? Latitude, double? Longitude, double PowerDbfs);

/// <summary>
/// An honest location estimate (SPEC §8.6, G9). A single moving receiver cannot triangulate, so
/// <see cref="UncertaintyM"/> is ALWAYS set and never a false point fix; it is surfaced in the UI.
/// </summary>
public sealed record LocationEstimate(double Latitude, double Longitude, double UncertaintyM);

/// <summary>
/// Produces geographic estimates from positioned observations (SPEC §8.6):
/// one obs → estimate = that position with a large floor uncertainty; multiple →
/// power-weighted centroid + uncertainty; no positioned obs → null (never 0,0).
/// </summary>
public interface IGeolocationEngine
{
    LocationEstimate? Estimate(IReadOnlyList<PositionedObservation> observations);
}
