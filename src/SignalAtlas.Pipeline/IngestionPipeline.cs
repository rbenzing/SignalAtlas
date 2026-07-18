using SignalAtlas.Collector;
using SignalAtlas.Domain;

namespace SignalAtlas.Pipeline;

/// <summary>
/// The live edge ingestion loop (SPEC §4.10 "real-time edge processing — the authoritative
/// result"). Streams an <see cref="ISampleSource"/> block-by-block through the whole edge
/// pipeline — collect → PSD → features → classify → correlate → anomaly — persisting each
/// observation (<see cref="IObservationRepository"/>) and signal (<see cref="ISignalWriter"/>).
///
/// DECODE IS OPTIONAL (SPEC §8.4): when <see cref="IDemodulator"/>s, an <see cref="IDecoderRegistry"/>,
/// an <see cref="IDeviceResolver"/>, and an <see cref="IDeviceRepository"/> are all supplied (the live
/// API wiring), the pipeline demodulates each block, decodes frames, and resolves/upserts/pushes the
/// determined devices. When any of those deps are absent, the decode stage is a no-op and the pipeline
/// stays an RF-only pass. Correlation always runs on RF features — every <see cref="CorrelationInput"/>
/// carries an EMPTY DecodedIdentifiers map today, exercising the weighted RF-fallback path (SPEC §8.5);
/// wiring decoded identifiers into correlation is a separate follow-up. When an
/// <see cref="IEmitterRepository"/>/<see cref="IAlertWriter"/> is supplied, correlated emitters are
/// upserted and raised alerts appended (and existing emitters loaded at the start of a run so
/// correlation is stateful, SPEC §7.8); emitter↔device links remain a follow-up. A run also returns
/// emitters/alerts in its <see cref="IngestionResult"/>.
///
/// Deterministic (P5): same source bytes + same clock/position → identical result.
/// </summary>
public sealed class IngestionPipeline
{
    private readonly ScanCollector _collector;
    private readonly ISignalProcessor _processor;
    private readonly IClassifier _classifier;
    private readonly ICorrelationEngine _correlation;
    private readonly IAnomalyEngine _anomaly;
    private readonly IObservationRepository _observations;
    private readonly ISignalWriter _signals;
    private readonly IEmitterRepository? _emitters;
    private readonly IAlertWriter? _alerts;
    private readonly ISpectrumBuffer? _spectrum;
    private readonly ILiveNotifier _notifier;
    private readonly IReadOnlyList<IDemodulator> _demodulators;
    private readonly IDecoderRegistry? _registry;
    private readonly IDeviceResolver? _resolver;
    private readonly IDeviceRepository? _devices;
    private readonly ICprPositionResolver? _cpr;

    public IngestionPipeline(
        string collectorId,
        IClock clock,
        IPositionSource position,
        ISignalProcessor processor,
        IClassifier classifier,
        ICorrelationEngine correlation,
        IAnomalyEngine anomaly,
        IObservationRepository observations,
        ISignalWriter signals,
        IEmitterRepository? emitters = null,
        IAlertWriter? alerts = null,
        ISpectrumBuffer? spectrum = null,
        ILiveNotifier? notifier = null,
        IEnumerable<IDemodulator>? demodulators = null,
        IDecoderRegistry? registry = null,
        IDeviceResolver? resolver = null,
        IDeviceRepository? devices = null,
        ICprPositionResolver? cpr = null)
    {
        _collector = new ScanCollector(collectorId, clock, position);
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _anomaly = anomaly ?? throw new ArgumentNullException(nameof(anomaly));
        _observations = observations ?? throw new ArgumentNullException(nameof(observations));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        // Emitter/alert stores are optional: when supplied, correlation is stateful across runs and
        // emitters/alerts are persisted; when null the run is self-contained (unit tests, dry runs).
        _emitters = emitters;
        _alerts = alerts;
        // Optional (SPEC §8.2): when supplied, each block's PSD is pushed for the Spectrum waterfall.
        _spectrum = spectrum;
        // Live push (SPEC §9.3): fire-and-forget broadcast of each artifact. No-op by default so
        // pipeline unit tests stay dependency-free.
        _notifier = notifier ?? NullLiveNotifier.Instance;
        // Optional decode stage (SPEC §8.4): demodulate IQ → frames → decode → determine devices.
        // Any null → the stage is a no-op (RF-only pipeline, unchanged).
        _demodulators = demodulators?.ToList() ?? [];
        _registry = registry;
        _resolver = resolver;
        _devices = devices;
        _cpr = cpr;
    }

    public IngestionResult Run(ISampleSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Load known emitters so correlation is STATEFUL across runs (SPEC §7.8): a re-run reuses
        // existing emitters instead of re-creating them. Empty store → identical to a fresh run (P5).
        var emitters = _emitters is null ? new List<Emitter>() : _emitters.All().ToList();
        var alerts = new List<Alert>();
        int observationCount = 0, signalCount = 0;

        long seq = 0;
        foreach (var block in source.Blocks())
        {
            if (ct.IsCancellationRequested)
                break;

            // 1. Collect → persist the raw observation (SPEC §8.1).
            var obs = _collector.Observe(block, seq);
            _observations.Add(obs);
            observationCount++;

            // 2. DSP → features → classification (SPEC §8.2, §8.3).
            var psd = _processor.ComputePsd(block);
            // Feed the Spectrum waterfall (SPEC §8.2) from the PSD already computed for this block.
            var frame = new SpectrumFrame(obs.Time, psd.CenterFreqHz, psd.SampleRateHz, psd.PowerDbfs);
            _spectrum?.Push(frame);
            _notifier.SpectrumFrame(frame);   // live push (SPEC §9.3 spectrum.frame).
            int durationMs = DurationMs(block);
            var features = _processor.ExtractFeatures(psd, durationMs);
            var cls = _classifier.Classify(features);

            // 3. Persist the classified signal. Deterministic id = seq + 1 (P5); with no obs Id
            //    surfaced by the store, the observation is referenced by the same monotonic key.
            long id = seq + 1;
            var signal = new Signal(
                Id: id,
                Time: obs.Time,
                ObservationId: id,
                EmitterId: null,
                DeviceId: null,
                Protocol: cls.Protocol,
                Confidence: cls.Confidence,
                Classifier: cls.Classifier,
                Evidence: cls.Evidence,
                CenterFreqHz: features.CenterFreqHz,
                BandwidthHz: features.Bandwidth3dBHz,
                DurationMs: durationMs,
                Features: FeatureMap(features, obs));
            _signals.Add(signal);
            signalCount++;
            _notifier.SignalCreated(signal);   // live push (SPEC §9.3 signal.created).

            // 4b. DECODE (SPEC §8.4): demodulate this block, decode frames, determine devices.
            if (_demodulators.Count > 0 && _registry is not null && _resolver is not null && _devices is not null)
            {
                var decoded = new List<DecodedFrame>();
                foreach (var demod in _demodulators)
                    foreach (var frameBytes in demod.Demodulate(block, features))
                    {
                        var outcome = _registry.Decode(demod.Protocol, frameBytes);
                        if (outcome.Success && outcome.Frame is not null)
                            decoded.Add(outcome.Frame);
                    }

                // One device per distinct primary identifier (e.g. ICAO): frames from the same
                // aircraft converge on the same deterministic device id → Upsert merges them.
                foreach (var group in decoded.GroupBy(PrimaryKey))
                {
                    var device = _resolver.Resolve(group.ToList());
                    if (device is not null)
                    {
                        // Stamp self-reported position from any airborne-position frame in this
                        // ICAO group (CPR resolved against the even/odd pairing cache).
                        if (_cpr is not null)
                        {
                            foreach (var f in group)
                            {
                                if (f.Cpr is not { } c) continue;
                                var pos = _cpr.Accept(group.Key.StartsWith("icao:") ? group.Key[5..] : group.Key,
                                    c.Odd, c.CprLat17, c.CprLon17, obs.Time);
                                if (c.AltitudeFt is { } altFt)
                                    device = device with { AltitudeFt = altFt };
                                if (pos is not null)
                                    device = device with { Latitude = pos.Latitude, Longitude = pos.Longitude };
                            }
                        }
                        _devices.Upsert(device);
                        _notifier.DeviceDetermined(device);
                    }
                }
            }

            // 4. Correlate on RF features ONLY — decoded identifiers aren't wired into correlation yet.
            var input = new CorrelationInput(
                Protocol: cls.Protocol,
                CenterFreqHz: features.CenterFreqHz,
                BandwidthHz: features.Bandwidth3dBHz,
                Latitude: obs.Latitude,
                Longitude: obs.Longitude,
                Time: obs.Time,
                DecodedIdentifiers: EmptyIdentifiers);
            var correlation = _correlation.Correlate(input, emitters);
            var emitter = correlation.Emitter;
            if (correlation.IsNew)
                emitters.Add(emitter);
            else
                ReplaceEmitter(emitters, emitter);
            _emitters?.Upsert(emitter);   // idempotent on the deterministic emitter id (SPEC §7.8).
            _notifier.EmitterUpdated(emitter);   // live push (SPEC §9.3 emitter.updated).

            // 5. Anomaly — baseline derived purely from whether the emitter was already known.
            //    KnownDevice stays false: emitter↔device linkage (correlating a determined device
            //    back onto its RF emitter) is a separate follow-up, independent of the decode stage.
            var evt = new AnomalyEvent(
                Time: obs.Time,
                EmitterId: emitter.Id,
                DeviceId: null,
                Protocol: cls.Protocol,
                PowerDbfs: obs.Power,
                Latitude: obs.Latitude,
                Longitude: obs.Longitude,
                Occupancy: features.DutyCycle);
            var baseline = new EmitterBaseline(
                KnownEmitter: !correlation.IsNew,
                KnownDevice: false,
                LastProtocol: null,
                LastLatitude: null,
                LastLongitude: null,
                BaselinePowerDbfs: null,
                BaselineOccupancy: null);
            var raised = _anomaly.Evaluate(evt, baseline);
            alerts.AddRange(raised);
            foreach (var alert in raised)
            {
                _alerts?.Add(alert);
                _notifier.AlertRaised(alert);   // live push (SPEC §9.3 alert.raised).
            }

            seq++;
        }

        return new IngestionResult(observationCount, signalCount, emitters.Count, alerts.Count, emitters, alerts);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyIdentifiers =
        new Dictionary<string, string>();

    // Ordered primary-identifier keys (mirrors DeviceResolver's precedence) used to group a block's
    // decoded frames per device before resolving. Falls back to protocol when none is present.
    private static readonly string[] PrimaryKeyOrder =
        { "icao", "bssid", "mac", "devaddr", "deveui", "ext_addr", "short_addr", "pan_id", "callsign", "pi", "ps" };

    private static string PrimaryKey(DecodedFrame f)
    {
        foreach (var key in PrimaryKeyOrder)
            if (f.Identifiers.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return $"{key}:{v}";
        return $"proto:{f.Protocol}";
    }

    // Block duration = samples / sample-rate (ms). Guards a zero sample rate.
    private static int DurationMs(IqBlock block) =>
        block.SampleRateHz <= 0 ? 0 : (int)Math.Round(block.SampleCount * 1000.0 / block.SampleRateHz);

    // Signal.Features is the numeric feature bag persisted alongside the classification (§7.2).
    private static IReadOnlyDictionary<string, double> FeatureMap(FeatureVector f, Observation obs) =>
        new Dictionary<string, double>
        {
            ["center_freq_hz"] = f.CenterFreqHz,
            ["bandwidth_3db_hz"] = f.Bandwidth3dBHz,
            ["bandwidth_20db_hz"] = f.Bandwidth20dBHz,
            ["peak_power_dbfs"] = f.PeakPowerDbfs,
            ["snr_db"] = f.SnrDb,
            ["duty_cycle"] = f.DutyCycle,
            ["power_dbfs"] = obs.Power,
        };

    private static void ReplaceEmitter(List<Emitter> emitters, Emitter updated)
    {
        for (int i = 0; i < emitters.Count; i++)
        {
            if (string.Equals(emitters[i].Id, updated.Id, StringComparison.Ordinal))
            {
                emitters[i] = updated;
                return;
            }
        }
        emitters.Add(updated);
    }
}
