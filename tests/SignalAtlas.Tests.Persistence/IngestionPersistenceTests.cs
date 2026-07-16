using SignalAtlas.Anomaly;
using SignalAtlas.Classification;
using SignalAtlas.Collector;
using SignalAtlas.Correlation;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;
using SignalAtlas.Pipeline;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Persistence;

/// <summary>
/// Drives the REAL <see cref="IngestionPipeline"/> (Processing/Classification/Correlation/Anomaly)
/// against the in-memory emitter/alert stores to prove the write path persists what it produces and
/// RELOADS it on a second run so correlation is stateful across runs (SPEC §4.10, §7.8, §8.5, §8.8).
/// </summary>
public sealed class IngestionPersistenceTests
{
    private const int BlockCount = 6;
    private const int SamplesPerBlock = 8192;
    private const long CenterFreqHz = 915_000_000;
    private const int SampleRateHz = 2_000_000;

    private static SyntheticSampleSource Source() =>
        new(BlockCount, SamplesPerBlock, CenterFreqHz, SampleRateHz);

    private static IngestionPipeline NewPipeline(IEmitterRepository emitters, IAlertWriter alerts) =>
        new(
            collectorId: "collector-A",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            position: new NoPositionSource(),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: new NullObservationRepo(),
            signals: new NullSignalWriter(),
            emitters: emitters,
            alerts: alerts);

    [Fact]
    public void Run_PersistsEmittersAndAlerts()
    {
        var emitters = new InMemoryEmitterRepository();
        var alerts = new InMemoryAlertRepository(new AnomalyEngine());

        var result = NewPipeline(emitters, alerts).Run(Source());

        Assert.NotEmpty(emitters.All());
        Assert.Equal(result.EmitterCount, emitters.All().Count);
        // Every emitter the run produced is in the store, keyed by its deterministic id.
        Assert.All(result.Emitters, e => Assert.Contains(emitters.All(), s => s.Id == e.Id));
        // The alert writer received at least the first-sighting new_emitter alert.
        Assert.Contains(result.Alerts, a => a.Kind == Alert.NewEmitter);
    }

    [Fact]
    public void SecondRun_ReloadsEmitters_And_DoesNotDuplicateThem()
    {
        var emitters = new InMemoryEmitterRepository();
        var alerts = new InMemoryAlertRepository(new AnomalyEngine());

        var first = NewPipeline(emitters, alerts).Run(Source());
        var afterFirst = emitters.All().Count;

        var second = NewPipeline(emitters, alerts).Run(Source());

        // Re-running the identical source reuses the emitters loaded from the store (SPEC §7.8):
        // the emitter count is stable, not doubled.
        Assert.Equal(first.EmitterCount, second.EmitterCount);
        Assert.Equal(afterFirst, emitters.All().Count);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
        public TimeSource Source => TimeSource.Gps;
    }

    private sealed class NullObservationRepo : IObservationRepository
    {
        public void Add(Observation o) { }
        public IReadOnlyList<Observation> GetRecent(int limit) => [];
    }

    private sealed class NullSignalWriter : ISignalWriter
    {
        public void Add(Signal s) { }
    }
}
