using SignalAtlas.Collector;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class ScanCollectorTests
{
    private static ScanCollector NewCollector(Position? pos = null) =>
        new("collector-A", new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            new StaticPositionSource(pos));

    // M0-T2 — one Observation per dwell (block).
    [Fact]
    public void Collect_EmitsOneObservationPerBlock()
    {
        var source = new SyntheticSampleSource(blockCount: 5, samplesPerBlock: 8, centerFreqHz: 915_000_000, sampleRateHz: 2_000_000);
        var obs = NewCollector().Collect(source).ToList();
        Assert.Equal(5, obs.Count);
    }

    // M0-T2 — per-collector monotonic sequence starting at 0.
    [Fact]
    public void Collect_AssignsMonotonicSequence()
    {
        var source = new SyntheticSampleSource(3, 8, 915_000_000, 2_000_000);
        var seqs = NewCollector().Collect(source).Select(o => o.Seq).ToList();
        Assert.Equal(new long[] { 0, 1, 2 }, seqs);
    }

    // M0-T2 — UTC time + source stamped from the clock.
    [Fact]
    public void Collect_StampsUtcTimeAndSource()
    {
        var source = new SyntheticSampleSource(1, 8, 915_000_000, 2_000_000);
        var o = Assert.Single(NewCollector().Collect(source));
        Assert.Equal(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero), o.Time);
        Assert.Equal(TimeSource.Gps, o.TimeSource);
    }

    // M0-T2 / G20 — power recorded relative by default.
    [Fact]
    public void Collect_RecordsRelativePowerByDefault()
    {
        var source = new SyntheticSampleSource(1, 8, 915_000_000, 2_000_000);
        var o = Assert.Single(NewCollector().Collect(source));
        Assert.Equal(PowerRef.Relative, o.PowerRef);
    }

    // M0-T2 / G13 — no fix → null coords + PositionQuality.None (never 0,0).
    [Fact]
    public void Collect_WithNoFix_EmitsNullCoordsAndNone()
    {
        var source = new SyntheticSampleSource(1, 8, 915_000_000, 2_000_000);
        var o = Assert.Single(NewCollector(pos: null).Collect(source));
        Assert.Null(o.Latitude);
        Assert.Null(o.Longitude);
        Assert.Equal(PositionQuality.None, o.PositionQuality);
    }

    // M0-T2 — a good fix is carried onto the observation.
    [Fact]
    public void Collect_WithFix_CarriesCoordsAndQuality()
    {
        var source = new SyntheticSampleSource(1, 8, 915_000_000, 2_000_000);
        var pos = new Position(42.36, -71.06, 10, PositionQuality.Good);
        var o = Assert.Single(NewCollector(pos).Collect(source));
        Assert.Equal(42.36, o.Latitude);
        Assert.Equal(-71.06, o.Longitude);
        Assert.Equal(PositionQuality.Good, o.PositionQuality);
    }

    // #13 — BandwidthHz was mislabeled as the ADC sample rate; a block carrying a ReceiverConfig
    // must yield the true analog passband (BasebandBwHz), and the config itself is carried through.
    [Fact]
    public void Observe_WithReceiverConfig_UsesBasebandBwAndCarriesConfig()
    {
        var receiverConfig = new ReceiverConfig(AmpEnable: true, LnaDb: 24, VgaDb: 20, BasebandBwHz: 1_750_000, BiasTee: false);
        var block = new IqBlock(915_000_000, 2_000_000, [0f, 0f], [0f, 0f], receiverConfig);

        var o = NewCollector().Observe(block, seq: 0);

        Assert.Equal(1_750_000, o.BandwidthHz);
        Assert.Equal(receiverConfig, o.ReceiverConfig);
    }

    // #13 — no ReceiverConfig (file/synthetic path) falls back to the sample rate and stays null.
    [Fact]
    public void Observe_WithNoReceiverConfig_FallsBackToSampleRate()
    {
        var block = new IqBlock(915_000_000, 2_000_000, [0f, 0f], [0f, 0f]);

        var o = NewCollector().Observe(block, seq: 0);

        Assert.Equal(2_000_000, o.BandwidthHz);
        Assert.Null(o.ReceiverConfig);
    }
}
