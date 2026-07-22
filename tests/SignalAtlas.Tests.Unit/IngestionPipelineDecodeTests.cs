using SignalAtlas.Anomaly;
using SignalAtlas.Classification;
using SignalAtlas.Correlation;
using SignalAtlas.Decode;
using SignalAtlas.Decode.Decoders;
using SignalAtlas.Decode.Demodulators;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;
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

    private static IngestionPipeline BuildWithCpr(CapturingDeviceRepo devices, CapturingNotifier notifier) =>
        new(
            collectorId: "adsb-test",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero)),
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
            devices: devices,
            cpr: new CprPositionResolver());

    [Fact]
    public void Run_AirbornePositionPair_StampsAircraftPosition()
    {
        // Even + odd canonical frames for ICAO 40621D → a fix near (52.26, 3.92).
        // Default lead/trail slots (4/4) — same pattern as the Stage-1 two-frame test.
        var even = AdsBModulator.Modulate(Hex("8D40621D58C382D690C8AC2863A7"));
        var odd = AdsBModulator.Modulate(Hex("8D40621D58C386435CC412692AD6"));
        var i = even.I.Concat(odd.I).ToArray();
        var q = even.Q.Concat(odd.Q).ToArray();
        var block = new IqBlock(1_090_000_000, 2_000_000, i, q);
        var devices = new CapturingDeviceRepo();

        BuildWithCpr(devices, new CapturingNotifier()).Run(new OneBlockSource(block));

        var d = Assert.Single(devices.Upserts);
        Assert.Equal("40621D", d.Identifiers["icao"]);
        Assert.NotNull(d.Latitude);
        Assert.InRange(d.Latitude!.Value, 52.2, 52.3);
        Assert.InRange(d.Longitude!.Value, 3.85, 3.95);
        Assert.Equal(38000, d.AltitudeFt);
    }

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

    // --- Bug #1 / #2 regression: real end-to-end grouping through the pipeline's PrimaryKeyOrder,
    // decoded via the REAL ZigbeeMacDecoder / BleAdvDecoder + REAL DeviceResolver. The demodulation
    // front-end for these protocols doesn't exist yet (only ADS-B's does, per CLAUDE.md's deferred
    // seam), so a fake IDemodulator supplies already-valid, FCS/CRC-passing frame bytes for a single
    // block — everything downstream (registry decode, grouping, resolve, upsert, notify) is real.

    private sealed class FakeDemodulator(string protocol, IReadOnlyList<byte[]> frames) : IDemodulator
    {
        public string Protocol => protocol;
        public IEnumerable<ReadOnlyMemory<byte>> Demodulate(IqBlock slice, FeatureVector features) =>
            frames.Select(f => (ReadOnlyMemory<byte>)f);
    }

    // A benign non-zero block (light tone) so the RF stages upstream of decode (PSD/features/
    // classify/correlate) don't hit degenerate all-zero DSP. Content is irrelevant to the fake
    // demodulators, which ignore it and yield pre-built frame bytes directly.
    private static IqBlock ToneBlock(long centerHz = 915_000_000, int sampleRateHz = 2_000_000, int samples = 4096)
    {
        var i = new float[samples];
        var q = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            double ph = 2.0 * Math.PI * 50_000 * n / sampleRateHz;
            i[n] = (float)(0.1 * Math.Cos(ph));
            q[n] = (float)(0.1 * Math.Sin(ph));
        }
        return new IqBlock(centerHz, sampleRateHz, i, q);
    }

    // Builds a valid (FCS-passing) IEEE 802.15.4 MAC frame: FrameControl(short src, no dest,
    // no PAN compression) + Seq + SrcPan(LE) + SrcAddr(LE, short) + FCS(CRC-16/KERMIT).
    private static byte[] BuildZigbeeFrame(ushort panId, ushort srcAddr, byte seq = 1)
    {
        var body = new List<byte>();
        ushort fc = 2 << 14; // srcMode=Short(2), destMode=None(0), panCompression=false.
        body.Add((byte)(fc & 0xFF));
        body.Add((byte)(fc >> 8));
        body.Add(seq);
        body.Add((byte)(panId & 0xFF));
        body.Add((byte)(panId >> 8));
        body.Add((byte)(srcAddr & 0xFF));
        body.Add((byte)(srcAddr >> 8));
        var fcs = ZigbeeMacDecoder.ComputeFcs(body.ToArray());
        body.Add((byte)(fcs & 0xFF));
        body.Add((byte)(fcs >> 8));
        return body.ToArray();
    }

    // Builds a valid (CRC-24-passing) BLE ADV_IND PDU: AccessAddr(4, unchecked) + header(2,
    // public TxAdd) + AdvA(6, wire = reverse of display) + CRC-24.
    private static byte[] BuildBleFrame(string displayMac)
    {
        var octets = displayMac.Split(':').Select(o => Convert.ToByte(o, 16)).ToArray();
        var wireMac = new byte[6];
        for (var i = 0; i < 6; i++) wireMac[i] = octets[5 - i];

        var pdu = new byte[8];
        pdu[0] = 0x00; // public address (TxAdd clear).
        pdu[1] = 6;    // payload length = AdvA only.
        Array.Copy(wireMac, 0, pdu, 2, 6);
        var crc = BleAdvDecoder.ComputeCrc24(pdu);

        var frame = new byte[4 + 8 + 3];
        frame[0] = frame[1] = frame[2] = frame[3] = 0xAA; // access address, not validated by the decoder.
        Array.Copy(pdu, 0, frame, 4, 8);
        frame[12] = (byte)(crc & 0xFF);
        frame[13] = (byte)((crc >> 8) & 0xFF);
        frame[14] = (byte)((crc >> 16) & 0xFF);
        return frame;
    }

    private static IngestionPipeline BuildWithSatDecoder(
        CapturingDeviceRepo devices, ISatelliteImageDecoder satDecoder, IAptImageStore aptImages) =>
        new(
            collectorId: "apt-test",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(null),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: new NoopObs(),
            signals: new NoopSignals(),
            devices: devices,
            satDecoder: satDecoder,
            aptImages: aptImages);

    // NOAA APT (Phase 1): a synthetic 8-line pass, modulated by AptModulator (the decoder's known-
    // good inverse fixture) and fed through the REAL pipeline — decoder, image store, device repo,
    // and live notifier all real end-to-end.
    [Fact]
    public void Run_NoaaAptPass_StoresImageAndDeterminesSatelliteDevice()
    {
        var rows = new byte[8][];
        for (int r = 0; r < 8; r++) { rows[r] = new byte[2080]; for (int c = 0; c < 2080; c++) rows[r][c] = (byte)(c & 0xFF); }
        var block = SignalAtlas.Decode.AptModulator.Modulate(rows, 137_100_000, 2_000_000);

        var devices = new CapturingDeviceRepo();
        var images = new AptImageStore();
        var pipeline = BuildWithSatDecoder(devices, new AptDecoder(), images);

        pipeline.Run(new OneBlockSource(block));

        var sat = devices.Upserts.FirstOrDefault(d => d.Protocol == "NOAA-APT");
        Assert.NotNull(sat);
        Assert.Equal("NOAA-19", sat!.PrimaryIdentifier);
        Assert.NotNull(images.Get(sat.Id));
    }

    private static IngestionPipeline BuildWithDemodulator(
        CapturingDeviceRepo devices, CapturingNotifier notifier, IDemodulator demodulator) =>
        new(
            collectorId: "decode-test",
            clock: new FixedClock(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero)),
            position: new StaticPositionSource(null),
            processor: new SignalProcessor(),
            classifier: new RuleBasedClassifier(),
            correlation: new WeightedCorrelationEngine(),
            anomaly: new AnomalyEngine(),
            observations: new NoopObs(),
            signals: new NoopSignals(),
            notifier: notifier,
            demodulators: [demodulator],
            registry: new DecoderRegistry([new ZigbeeMacDecoder(), new BleAdvDecoder()]),
            resolver: new DeviceResolver(new OuiLookup()),
            devices: devices);

    // Bug #1 regression: two Zigbee nodes on the SAME PAN (distinct src_addr) must group into TWO
    // devices, not collapse onto pan_id.
    [Fact]
    public void Run_TwoZigbeeNodesSamePan_ProducesTwoDevices()
    {
        var frames = new List<byte[]>
        {
            BuildZigbeeFrame(panId: 0x1234, srcAddr: 0xAAAA),
            BuildZigbeeFrame(panId: 0x1234, srcAddr: 0xBBBB),
        };
        var devices = new CapturingDeviceRepo();
        var notifier = new CapturingNotifier();

        BuildWithDemodulator(devices, notifier, new FakeDemodulator("Zigbee", frames))
            .Run(new OneBlockSource(ToneBlock()));

        Assert.Equal(2, devices.Upserts.Count);
        var srcAddrs = devices.Upserts.Select(d => d.Identifiers["src_addr"]).ToHashSet();
        Assert.Equal(new HashSet<string> { "AAAA", "BBBB" }, srcAddrs);
        Assert.All(devices.Upserts, d => Assert.Equal("1234", d.Identifiers["pan_id"]));
    }

    // Bug #2 regression: two BLE advertisers with distinct adva must group into TWO devices, not
    // collapse onto the `proto:BLE` fallback bucket.
    [Fact]
    public void Run_TwoBleAdvertisers_ProducesTwoDevices()
    {
        var frames = new List<byte[]>
        {
            BuildBleFrame("3C:5A:B4:00:00:01"),
            BuildBleFrame("3C:5A:B4:00:00:02"),
        };
        var devices = new CapturingDeviceRepo();
        var notifier = new CapturingNotifier();

        BuildWithDemodulator(devices, notifier, new FakeDemodulator("BLE", frames))
            .Run(new OneBlockSource(ToneBlock()));

        Assert.Equal(2, devices.Upserts.Count);
        var advas = devices.Upserts.Select(d => d.Identifiers["adva"]).ToHashSet();
        Assert.Equal(new HashSet<string> { "3c:5a:b4:00:00:01", "3c:5a:b4:00:00:02" }, advas);
    }
}
