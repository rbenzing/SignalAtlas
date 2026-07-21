namespace SignalAtlas.Domain;

/// <summary>The receiver configuration that shaped a capture (SPEC §8.1 provenance) — the HackRF's
/// three RX gain stages, the baseband anti-alias filter width, and antenna-port bias-tee. Null on an
/// IqBlock/Observation from a source with no real receiver (file replay, synthetic).</summary>
public sealed record ReceiverConfig(bool AmpEnable, int LnaDb, int VgaDb, int BasebandBwHz, bool BiasTee);
