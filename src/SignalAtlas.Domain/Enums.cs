namespace SignalAtlas.Domain;

/// <summary>Source of the UTC timestamp on an observation (SPEC §5.4, G14).</summary>
public enum TimeSource
{
    Gps,
    Ntp,
    Host
}

/// <summary>Quality of the position fix on an observation (SPEC §8.1, G13).</summary>
public enum PositionQuality
{
    None,
    Stale,
    Good
}

/// <summary>Whether recorded power is uncalibrated dBFS-relative or converted dBm (SPEC §4.5, G20).</summary>
public enum PowerRef
{
    Relative,
    Calibrated
}
