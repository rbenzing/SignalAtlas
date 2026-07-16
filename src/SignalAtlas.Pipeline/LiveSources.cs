using SignalAtlas.Domain;

namespace SignalAtlas.Pipeline;

/// <summary>
/// Wall-clock <see cref="IClock"/> for live collection when no GPS/NTP-disciplined clock is wired
/// (SPEC §5.4 fallback: GPS→NTP→host). Stamps <see cref="TimeSource.Host"/> so downstream consumers
/// know the time is only host-accurate.
/// </summary>
public sealed class HostClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSource Source => TimeSource.Host;
}

/// <summary>
/// Null-object <see cref="IPositionSource"/> for GPS-denied / no-GPS operation (SPEC §8.1, G13):
/// always reports no fix, which the collector maps to null coords + <see cref="PositionQuality.None"/>.
/// </summary>
public sealed class NoPositionSource : IPositionSource
{
    public Position? Current => null;
}
