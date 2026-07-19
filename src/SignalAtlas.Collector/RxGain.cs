namespace SignalAtlas.Collector;

/// <summary>The HackRF One's three independent RX gain stages (SPEC §4.1). RF amp is a 0/+14 dB
/// enable; LNA is 0-40 dB in 8 dB steps; VGA is 0-62 dB in 2 dB steps. Modeled separately because a
/// single dB scalar cannot express the amp enable or the distinct step sizes.</summary>
public readonly record struct RxGain(bool AmpEnable, int LnaDb, int VgaDb)
{
    /// <summary>A sensible general-survey default: amp off, LNA 16 dB, VGA 20 dB.</summary>
    public static readonly RxGain Default = new(false, 16, 20);
}
