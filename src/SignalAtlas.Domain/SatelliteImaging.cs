namespace SignalAtlas.Domain;

/// <summary>Result of an in-progress/complete APT pass: the satellite Device plus the decoded
/// greyscale image as a PNG (in-memory content only — never persisted, never egressed, per the
/// invariant-#3 carve-out for public non-personal broadcast imagery).</summary>
public sealed record SatellitePass(Device Device, byte[] PngImage, int Lines, double SyncQuality);

/// <summary>Parallel pass-decoder seam (SPEC §8.4 / NOAA APT Phase 1). Stateful per stream: it
/// accumulates one image over a multi-minute pass, so DI registration MUST be Transient
/// (landmine #10). Returns null until sync locks and enough lines are assembled.</summary>
public interface ISatelliteImageDecoder
{
    bool AppliesTo(long centerFreqHz);
    SatellitePass? Accept(IqBlock block, FeatureVector features, DateTimeOffset time);
}

/// <summary>Bounded in-memory store of decoded APT images keyed by device id. Thread-safe singleton.
/// Content is NEVER persisted to disk/DB (invariant-#3 carve-out).</summary>
public interface IAptImageStore
{
    void Put(string deviceId, byte[] png);
    byte[]? Get(string deviceId);
}
