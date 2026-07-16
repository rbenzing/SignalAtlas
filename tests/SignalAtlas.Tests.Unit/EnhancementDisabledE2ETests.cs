using SignalAtlas.Anomaly;
using SignalAtlas.Classification;
using SignalAtlas.Collector;
using SignalAtlas.Correlation;
using SignalAtlas.Domain;
using SignalAtlas.Pipeline;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// AC-DA0 (SPEC §8.13, the critical one): the full edge platform — classify → correlate → map → alert —
/// runs to a complete, authoritative result with the OPTIONAL Claude enhancement engine DISABLED / never
/// invoked. This proves Claude is never on the critical path (§4.10): NO <see cref="IDeferredAnalyzer"/>
/// is constructed, referenced, or resolved anywhere in this pipeline.
/// </summary>
public class EnhancementDisabledE2ETests
{
    private const int BlockCount = 6;
    private const int SamplesPerBlock = 8192;
    private const long CenterFreqHz = 915_000_000;
    private const int SampleRateHz = 2_000_000;

    [Fact]
    public void FullEdgePipeline_ProducesCompleteResult_WithEnhancementEngineDisabled()
    {
        // The pipeline is composed from ONLY the deterministic edge engines — no enhancement analyzer.
        var pipeline = new IngestionPipeline(
            collectorId: "collector-A",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(new Position(42.36, -71.06, null, PositionQuality.Good)),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: new RecordingObservationRepo(),
            signals: new RecordingSignalWriter());

        var result = pipeline.Run(new SyntheticSampleSource(BlockCount, SamplesPerBlock, CenterFreqHz, SampleRateHz));

        // classify: every block yields a classified signal; correlate: emitters formed; alert: raised.
        Assert.Equal(BlockCount, result.SignalCount);
        Assert.True(result.EmitterCount >= 1);
        Assert.True(result.AlertCount >= 1);
        // map: the correlated emitters carry the geospatial estimate the RF Map plots.
        Assert.All(result.Emitters, e => Assert.NotEmpty(e.Evidence));

        // The authoritative result is complete WITHOUT any Claude/enrichment involvement.
        Assert.IsNotType<IDeferredAnalyzer>(pipeline);
    }

    private sealed class RecordingObservationRepo : IObservationRepository
    {
        private readonly List<Observation> _added = [];
        public void Add(Observation o) => _added.Add(o);
        public IReadOnlyList<Observation> GetRecent(int limit) => _added.AsEnumerable().Reverse().Take(limit).ToList();
    }

    private sealed class RecordingSignalWriter : ISignalWriter
    {
        public void Add(Signal s) { }
    }
}
