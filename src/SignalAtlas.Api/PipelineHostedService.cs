using SignalAtlas.Collector;
using SignalAtlas.Domain;
using SignalAtlas.Pipeline;

namespace SignalAtlas.Api;

/// <summary>
/// Runs one bounded live-ingestion pass at startup (SPEC §4.10 real-time edge processing) — but
/// only when <c>Ingestion:Enabled == "true"</c>. Gated OFF by default so the contract/integration
/// tests (which boot via WebApplicationFactory with no ingestion config) are never disturbed.
///
/// Source selection (first match wins):
///   1. a registered HackRF <see cref="ISampleSource"/> (real hardware, SPEC §4.1), else
///   2. <c>Ingestion:IqFile</c> → a <see cref="FileSampleSource"/> replaying that .iq file, else
///   3. a bounded <see cref="SyntheticSampleSource"/> (deterministic, zero-hardware).
///
/// The run is bounded (the source is finite) and executes exactly once — it does NOT loop forever.
/// </summary>
public sealed class PipelineHostedService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<PipelineHostedService> logger) : BackgroundService
{
    // Field defaults mirror the collector survey rig (SPEC §4.1/§4.4).
    private const long DefaultCenterFreqHz = 915_000_000;
    private const int DefaultSampleRateHz = 2_000_000;
    private const int DefaultSamplesPerBlock = 8192;
    private const int DefaultSyntheticBlocks = 64;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(config["Ingestion:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask; // Disabled: the platform runs on seeded/read data only.

        // A fresh scope so scoped (DB-mode) repositories resolve correctly.
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var source = ResolveSource(sp);

        // Optional bounded-queue backpressure (SPEC §4.9 G27, NFR-T1/C3): when a capacity is set,
        // interpose a drop-oldest bounded buffer between source and processing; drops are counted and
        // surfaced at /metrics. The default (unset/0) keeps the direct synchronous path.
        int boundedCapacity = config.GetValue("Ingestion:BoundedCapacity", 0);
        BoundedSampleSource? bounded = null;
        if (boundedCapacity > 0)
        {
            bounded = new BoundedSampleSource(source, boundedCapacity);
            source = bounded;
        }

        var pipeline = new IngestionPipeline(
            collectorId: config["Ingestion:CollectorId"] ?? "edge-1",
            clock: sp.GetService<IClock>() ?? new HostClock(),
            position: sp.GetService<IPositionSource>() ?? new NoPositionSource(),
            processor: sp.GetRequiredService<ISignalProcessor>(),
            classifier: sp.GetRequiredService<IClassifier>(),
            correlation: sp.GetRequiredService<ICorrelationEngine>(),
            anomaly: sp.GetRequiredService<IAnomalyEngine>(),
            observations: sp.GetRequiredService<IObservationRepository>(),
            signals: sp.GetRequiredService<ISignalWriter>(),
            emitters: sp.GetService<IEmitterRepository>(),
            alerts: sp.GetService<IAlertWriter>(),
            spectrum: sp.GetService<ISpectrumBuffer>(),
            notifier: sp.GetService<ILiveNotifier>(),   // live push (SPEC §9.3); null-safe if unregistered.
            demodulators: sp.GetServices<IDemodulator>(),
            registry: sp.GetService<IDecoderRegistry>(),
            resolver: sp.GetService<IDeviceResolver>(),
            devices: sp.GetService<IDeviceRepository>(),
            cpr: sp.GetService<ICprPositionResolver>(),
            satDecoder: sp.GetService<ISatelliteImageDecoder>(),
            aptImages: sp.GetService<IAptImageStore>());

        // Honors the host stopping token (SPEC §5.5): a shutdown cancels the in-flight Run promptly and
        // disposes the source (the source enumerator's finally releases the device).
        var result = pipeline.Run(source, stoppingToken);

        if (bounded is not null)
            sp.GetService<IngestionDropsMonitor>()?.Set(bounded.Drops);

        logger.LogInformation(
            "Live ingestion complete: {Observations} observations, {Signals} signals, {Emitters} emitters, {Alerts} alerts, {Drops} drops.",
            result.ObservationCount, result.SignalCount, result.EmitterCount, result.AlertCount, bounded?.Drops ?? 0);

        return Task.CompletedTask;
    }

    private ISampleSource ResolveSource(IServiceProvider sp)
    {
        // 1. Real HackRF, if the collector wiring registered one.
        if (sp.GetService<ISampleSource>() is { } hardware)
            return hardware;

        long centerHz = config.GetValue("Ingestion:CenterFreqHz", DefaultCenterFreqHz);
        int sampleRateHz = config.GetValue("Ingestion:SampleRateHz", DefaultSampleRateHz);
        int samplesPerBlock = config.GetValue("Ingestion:SamplesPerBlock", DefaultSamplesPerBlock);

        // 2. A replay .iq file, if configured.
        var iqFile = config["Ingestion:IqFile"];
        if (!string.IsNullOrWhiteSpace(iqFile) && File.Exists(iqFile))
        {
            var raw = File.ReadAllBytes(iqFile);
            return new FileSampleSource(raw, samplesPerBlock, centerHz, sampleRateHz);
        }

        // 3. Bounded synthetic fallback — deterministic, zero-hardware.
        int blocks = config.GetValue("Ingestion:SyntheticBlocks", DefaultSyntheticBlocks);
        return new SyntheticSampleSource(blocks, samplesPerBlock, centerHz, sampleRateHz);
    }
}
