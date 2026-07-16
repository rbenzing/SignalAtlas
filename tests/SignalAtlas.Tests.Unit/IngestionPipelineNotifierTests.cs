using SignalAtlas.Anomaly;
using SignalAtlas.Classification;
using SignalAtlas.Collector;
using SignalAtlas.Correlation;
using SignalAtlas.Domain;
using SignalAtlas.Pipeline;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// Drives the REAL edge pipeline (SPEC §4.10) over a synthetic source with a capturing
/// <see cref="ILiveNotifier"/> and asserts the live-push seam (SPEC §9.3) fires once per produced
/// artifact: N signals, at least one emitter update, at least one alert. Decode is deferred so no
/// device is determined — DeviceDetermined is not expected here.
/// </summary>
public class IngestionPipelineNotifierTests
{
    private const int BlockCount = 6;
    private const int SamplesPerBlock = 8192;
    private const long CenterFreqHz = 915_000_000;
    private const int SampleRateHz = 2_000_000;

    private static SyntheticSampleSource Source() =>
        new(BlockCount, SamplesPerBlock, CenterFreqHz, SampleRateHz);

    [Fact]
    public void Run_BroadcastsSignalEmitterAndAlertEvents()
    {
        var notifier = new CapturingLiveNotifier();

        var pipeline = new IngestionPipeline(
            collectorId: "collector-A",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(null),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: new NoopObservationRepo(),
            signals: new NoopSignalWriter(),
            notifier: notifier);

        pipeline.Run(Source());

        Assert.Equal(BlockCount, notifier.Signals.Count);
        Assert.True(notifier.Emitters.Count >= 1, "expected at least one emitterUpdated broadcast");
        Assert.True(notifier.Alerts.Count >= 1, "expected at least one alertRaised broadcast");
        Assert.Contains(notifier.Alerts, a => a.Kind == Alert.NewEmitter);
    }

    private sealed class CapturingLiveNotifier : ILiveNotifier
    {
        public List<Signal> Signals { get; } = [];
        public List<Emitter> Emitters { get; } = [];
        public List<Alert> Alerts { get; } = [];
        public List<SpectrumFrame> Frames { get; } = [];
        public List<Device> Devices { get; } = [];

        public void SignalCreated(Signal signal) => Signals.Add(signal);
        public void EmitterUpdated(Emitter emitter) => Emitters.Add(emitter);
        public void AlertRaised(Alert alert) => Alerts.Add(alert);
        public void SpectrumFrame(SpectrumFrame frame) => Frames.Add(frame);
        public void DeviceDetermined(Device device) => Devices.Add(device);
    }

    private sealed class NoopObservationRepo : IObservationRepository
    {
        public void Add(Observation o) { }
        public IReadOnlyList<Observation> GetRecent(int limit) => [];
    }

    private sealed class NoopSignalWriter : ISignalWriter
    {
        public void Add(Signal s) { }
    }
}
