using SignalAtlas.Anomaly;
using SignalAtlas.Classification;
using SignalAtlas.Correlation;
using SignalAtlas.Decode;
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;
using SignalAtlas.Pipeline;
using SignalAtlas.Processing;

namespace SignalAtlas.Tests.Unit;

public class IngestionPipelineDecodeTests
{
    private const string GoldenHex = "8D4840D6202CC371C32CE0576098";

    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    // Single-block source carrying the caller's IqBlock (SPEC ISampleSource, receive-only).
    private sealed class OneBlockSource(IqBlock block) : ISampleSource
    {
        public IEnumerable<IqBlock> Blocks() { yield return block; }
    }

    private sealed class CapturingDeviceRepo : IDeviceRepository
    {
        public List<Device> Upserts { get; } = [];
        public IReadOnlyList<Device> GetDevices(int limit = 100) => Upserts;
        public void Upsert(Device device) => Upserts.Add(device);
    }

    private sealed class CapturingNotifier : ILiveNotifier
    {
        public List<Device> Devices { get; } = [];
        public void SignalCreated(Signal signal) { }
        public void EmitterUpdated(Emitter emitter) { }
        public void AlertRaised(Alert alert) { }
        public void SpectrumFrame(SpectrumFrame frame) { }
        public void DeviceDetermined(Device device) => Devices.Add(device);
    }

    private sealed class NoopObs : IObservationRepository
    {
        public void Add(Observation o) { }
        public IReadOnlyList<Observation> GetRecent(int limit) => [];
    }

    private sealed class NoopSignals : ISignalWriter { public void Add(Signal s) { } }

    private static IngestionPipeline Build(CapturingDeviceRepo devices, CapturingNotifier notifier) =>
        new(
            collectorId: "adsb-test",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(null),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: new NoopObs(),
            signals: new NoopSignals(),
            notifier: notifier,
            demodulators: [new AdsBDemodulator()],
            registry: new DecoderRegistry([new AdsBDecoder()]),
            resolver: new DeviceResolver(new OuiLookup()),
            devices: devices);

    [Fact]
    public void Run_BlockWithAdsBFrame_UpsertsAircraftAndPushesDeviceDetermined()
    {
        // Frame embedded in an 8192-sample @ 2 MS/s block at 1090 MHz (light noise avoids all-zero DSP).
        var block = AdsBModulator.Modulate(Hex(GoldenHex), trailSlots: 7948, noiseSigma: 0.01);
        var devices = new CapturingDeviceRepo();
        var notifier = new CapturingNotifier();

        Build(devices, notifier).Run(new OneBlockSource(block));

        var device = Assert.Single(devices.Upserts);
        Assert.Equal("Aircraft", device.DeviceType);
        Assert.Equal("4840D6", device.Identifiers["icao"]);
        Assert.Contains(notifier.Devices, d => d.Identifiers["icao"] == "4840D6");
    }

    [Fact]
    public void Run_BlockWithoutAdsB_DeterminesNoDevice()
    {
        // A 915 MHz block → demod self-gates off → no devices, no regression to the RF pipeline.
        var block = AdsBModulator.Modulate(Hex(GoldenHex), centerFreqHz: 915_000_000,
            trailSlots: 7948, noiseSigma: 0.01);
        var devices = new CapturingDeviceRepo();
        var notifier = new CapturingNotifier();

        Build(devices, notifier).Run(new OneBlockSource(block));

        Assert.Empty(devices.Upserts);
        Assert.Empty(notifier.Devices);
    }
}
