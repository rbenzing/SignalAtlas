using SignalAtlas.Domain;

namespace SignalAtlas.Collector;

/// <summary>
/// Consumes an <see cref="ISampleSource"/> and emits one <see cref="Observation"/> per dwell
/// (block), stamping UTC time + source, a per-collector monotonic sequence, relative power,
/// and the current position (null → <see cref="PositionQuality.None"/>). SPEC §8.1.
/// </summary>
public sealed class ScanCollector
{
    private readonly string _collectorId;
    private readonly IClock _clock;
    private readonly IPositionSource _position;

    public ScanCollector(string collectorId, IClock clock, IPositionSource position)
    {
        _collectorId = collectorId;
        _clock = clock;
        _position = position;
    }

    public IEnumerable<Observation> Collect(ISampleSource source)
    {
        long seq = 0;
        foreach (var block in source.Blocks())
        {
            yield return Observe(block, seq);
            seq++;
        }
    }

    /// <summary>
    /// Turns a single dwell (block) into an <see cref="Observation"/> at the given sequence number
    /// (SPEC §8.1). Extracted so the live ingestion pipeline can drive the collector one block at a
    /// time while <see cref="Collect"/> keeps its streaming contract. Pure given the injected
    /// clock/position (P5).
    /// </summary>
    public Observation Observe(IqBlock block, long seq)
    {
        var pos = _position.Current;
        return new Observation(
            Time: _clock.UtcNow,
            TimeSource: _clock.Source,
            CollectorId: _collectorId,
            Seq: seq,
            FrequencyHz: block.CenterFreqHz,
            // True analog passband when the block carries receiver provenance; sample-rate fallback
            // for sources with no real receiver (file replay, synthetic) where no filter width is known.
            BandwidthHz: block.ReceiverConfig?.BasebandBwHz ?? block.SampleRateHz,
            Power: RelativePowerDbfs(block),
            PowerRef: PowerRef.Relative,
            SnrDb: null,
            Latitude: pos?.Latitude,
            Longitude: pos?.Longitude,
            PositionQuality: pos?.Quality ?? PositionQuality.None,
            IqRef: null,
            CorrelationId: DeterministicGuid.From($"{_collectorId}:{seq}"),
            ReceiverConfig: block.ReceiverConfig);
    }

    /// <summary>Mean-power of the block expressed as dBFS relative to full scale (G20).</summary>
    private static double RelativePowerDbfs(IqBlock block)
    {
        double sumSquares = 0;
        for (int s = 0; s < block.SampleCount; s++)
            sumSquares += (double)block.I[s] * block.I[s] + (double)block.Q[s] * block.Q[s];
        if (block.SampleCount == 0) return -160.0;
        double meanSquare = sumSquares / block.SampleCount;
        return meanSquare <= 0 ? -160.0 : 10.0 * Math.Log10(meanSquare);
    }
}
