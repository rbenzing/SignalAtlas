using SignalAtlas.Anomaly;
using SignalAtlas.Classification;
using SignalAtlas.Collector;
using SignalAtlas.Correlation;
using SignalAtlas.Domain;
using SignalAtlas.Pipeline;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// Graceful shutdown (SPEC §5.5): cancelling the host stopping token mid-run stops processing
/// promptly and disposes the source (the source enumerator's finally releases the device).
/// Deterministic — the source cancels its own token after a fixed number of blocks.
/// </summary>
public class GracefulShutdownTests
{
    [Fact]
    public void CancellingMidRun_StopsPromptly_AndDisposesSource()
    {
        using var cts = new CancellationTokenSource();
        var source = new CancelAfterSource(cts, cancelAfter: 3);

        var obs = new CountingObservationRepo();
        var pipeline = new IngestionPipeline(
            collectorId: "collector-A",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(null),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: obs,
            signals: new NullSignalWriter());

        var result = pipeline.Run(source, cts.Token);

        Assert.True(source.Disposed);                 // source released on cancellation.
        Assert.Equal(3, result.ObservationCount);     // only pre-cancel blocks processed.
        Assert.True(source.Yielded < 100);            // stopped promptly, not run to the (large) end.
    }

    private sealed class CancelAfterSource(CancellationTokenSource cts, int cancelAfter) : ISampleSource
    {
        public bool Disposed { get; private set; }
        public int Yielded { get; private set; }

        public IEnumerable<IqBlock> Blocks()
        {
            var template = new SyntheticSampleSource(1, 8192, 915_000_000, 2_000_000).Blocks().First();
            try
            {
                for (int i = 0; i < 100_000; i++)
                {
                    if (i == cancelAfter) cts.Cancel();
                    Yielded++;
                    yield return template;
                }
            }
            finally
            {
                Disposed = true;
            }
        }
    }

    private sealed class CountingObservationRepo : IObservationRepository
    {
        public int Count { get; private set; }
        public void Add(Observation o) => Count++;
        public IReadOnlyList<Observation> GetRecent(int limit) => [];
    }

    private sealed class NullSignalWriter : ISignalWriter
    {
        public void Add(Signal s) { }
    }
}
