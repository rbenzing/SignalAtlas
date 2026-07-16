using SignalAtlas.Anomaly;
using SignalAtlas.Collector;
using SignalAtlas.Correlation;
using SignalAtlas.Domain;
using SignalAtlas.Ml;
using SignalAtlas.Pipeline;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M9 — rules↔ML hot-swap (SPEC §8.9 test list). The ML classifier drops into the SAME ingestion
/// path behind <see cref="IClassifier"/> and keeps the pipeline green: every signal still carries a
/// protocol, a classifier id and non-empty evidence (mirrors <see cref="IngestionPipelineTests"/>).
/// </summary>
public class MlHotSwapTests
{
    private const int BlockCount = 6;
    private const int SamplesPerBlock = 8192;
    private const long CenterFreqHz = 915_000_000;
    private const int SampleRateHz = 2_000_000;

    private static SyntheticSampleSource Source() =>
        new(BlockCount, SamplesPerBlock, CenterFreqHz, SampleRateHz);

    private static IngestionPipeline NewPipeline(IClassifier classifier, RecordingSignalWriter sig) =>
        new(
            collectorId: "collector-ml",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(null),
            processor: new SignalProcessor(),
            classifier: classifier,
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: new RecordingObservationRepo(),
            signals: sig);

    [Fact]
    public void Run_WithMlClassifier_KeepsPipelineGreen()
    {
        var sig = new RecordingSignalWriter();
        var result = NewPipeline(new MlClassifier(), sig).Run(Source());

        Assert.Equal(BlockCount, result.SignalCount);
        Assert.All(sig.Added, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Protocol));
            Assert.Equal(MlClassifier.ClassifierName, s.Classifier);
            Assert.NotEmpty(s.Evidence);
        });
    }

    private sealed class RecordingObservationRepo : IObservationRepository
    {
        public List<Observation> Added { get; } = [];
        public void Add(Observation o) => Added.Add(o);
        public IReadOnlyList<Observation> GetRecent(int limit) =>
            Added.AsEnumerable().Reverse().Take(limit).ToList();
    }

    private sealed class RecordingSignalWriter : ISignalWriter
    {
        public List<Signal> Added { get; } = [];
        public void Add(Signal s) => Added.Add(s);
    }
}
