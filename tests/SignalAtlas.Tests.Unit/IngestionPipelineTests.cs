using SignalAtlas.Anomaly;
using SignalAtlas.Classification;
using SignalAtlas.Collector;
using SignalAtlas.Correlation;
using SignalAtlas.Domain;
using SignalAtlas.Pipeline;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// Integration-style unit tests for the live edge ingestion loop (SPEC §4.10). Uses the REAL
/// Processing / Classification / Correlation / Anomaly engines with in-memory repos — no hardware,
/// no database, deterministic (P5). Decode is deferred, so correlation runs on RF features only.
/// </summary>
public class IngestionPipelineTests
{
    private const int BlockCount = 6;
    private const int SamplesPerBlock = 8192;
    private const long CenterFreqHz = 915_000_000;
    private const int SampleRateHz = 2_000_000;

    private static IngestionPipeline NewPipeline(
        RecordingObservationRepo obs, RecordingSignalWriter sig) =>
        new(
            collectorId: "collector-A",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(null),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: obs,
            signals: sig);

    private static SyntheticSampleSource Source() =>
        new(BlockCount, SamplesPerBlock, CenterFreqHz, SampleRateHz);

    [Fact]
    public void Run_PersistsOneObservationAndOneSignalPerBlock()
    {
        var obs = new RecordingObservationRepo();
        var sig = new RecordingSignalWriter();

        var result = NewPipeline(obs, sig).Run(Source());

        Assert.Equal(BlockCount, result.ObservationCount);
        Assert.Equal(BlockCount, result.SignalCount);
        Assert.Equal(BlockCount, obs.Added.Count);
        Assert.Equal(BlockCount, sig.Added.Count);
    }

    [Fact]
    public void Run_EverySignalCarriesClassificationAndNonEmptyEvidence()
    {
        var sig = new RecordingSignalWriter();

        NewPipeline(new RecordingObservationRepo(), sig).Run(Source());

        Assert.All(sig.Added, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Protocol));
            Assert.False(string.IsNullOrWhiteSpace(s.Classifier));
            Assert.NotEmpty(s.Evidence);
        });
    }

    [Fact]
    public void Run_ReusesOneEmitterForASteadyTone_WithEvidence()
    {
        var result = NewPipeline(new RecordingObservationRepo(), new RecordingSignalWriter())
            .Run(Source());

        // A steady tone correlates back to a single emitter (EmitterCount < N), which is the
        // whole point of RF-fallback correlation without decoded identifiers (SPEC §8.5).
        Assert.True(result.EmitterCount >= 1);
        Assert.True(result.EmitterCount < BlockCount);
        Assert.All(result.Emitters, e => Assert.NotEmpty(e.Evidence));
    }

    [Fact]
    public void Run_FirstSightingRaisesANewEmitterAlert()
    {
        var result = NewPipeline(new RecordingObservationRepo(), new RecordingSignalWriter())
            .Run(Source());

        Assert.True(result.AlertCount >= 1);
        Assert.Contains(result.Alerts, a => a.Kind == Alert.NewEmitter);
    }

    [Fact]
    public void Run_IsDeterministic_AcrossTwoRunsOverTheSameSource()
    {
        var a = NewPipeline(new RecordingObservationRepo(), new RecordingSignalWriter()).Run(Source());
        var b = NewPipeline(new RecordingObservationRepo(), new RecordingSignalWriter()).Run(Source());

        Assert.Equal(a.ObservationCount, b.ObservationCount);
        Assert.Equal(a.SignalCount, b.SignalCount);
        Assert.Equal(a.EmitterCount, b.EmitterCount);
        Assert.Equal(a.AlertCount, b.AlertCount);
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
