using SignalAtlas.Collector;

namespace SignalAtlas.Tests.Unit;

public class HackRfSourceTests
{
    /// <summary>Fake HackRF returning a fixed queue of interleaved-IQ byte buffers, then empty (end-of-stream).</summary>
    private sealed class FakeHackRfDevice : IHackRfDevice
    {
        private readonly Queue<byte[]> _buffers;
        public FakeHackRfDevice(bool available, params byte[][] buffers)
        {
            IsAvailable = available;
            _buffers = new Queue<byte[]>(buffers);
        }

        public bool IsAvailable { get; }
        public bool Opened { get; private set; }
        public bool Closed { get; private set; }
        public RxGain? OpenedGain { get; private set; }
        public int? OpenedBasebandBwHz { get; private set; }
        public bool? OpenedBiasTee { get; private set; }

        public void OpenReceive(long centerFreqHz, int sampleRateHz, RxGain gain, int basebandBwHz, bool biasTee)
        {
            Opened = true;
            OpenedGain = gain;
            OpenedBasebandBwHz = basebandBwHz;
            OpenedBiasTee = biasTee;
        }
        public ReadOnlyMemory<byte> ReadBlock() =>
            _buffers.Count > 0 ? _buffers.Dequeue() : ReadOnlyMemory<byte>.Empty;
        public void Close() => Closed = true;
    }

    // M8 — an available HackRF: two known interleaved-IQ buffers → 2 blocks with normalized (i/128f) samples.
    [Fact]
    public void Blocks_DecodeInterleavedSignedBytes_FromAvailableDevice()
    {
        // Buffer 1: I0=0,Q0=64,I1=-128,Q1=-64 ; Buffer 2: I0=127,Q0=-1,I1=32,Q1=-32
        var b1 = new byte[] { 0, 64, 128, 192 };
        var b2 = new byte[] { 127, 255, 32, 224 };
        var device = new FakeHackRfDevice(available: true, b1, b2);
        var gain = new RxGain(AmpEnable: true, LnaDb: 24, VgaDb: 30);
        var src = new HackRfSampleSource(
            device, centerFreqHz: 915_000_000, sampleRateHz: 2_000_000, gain: gain,
            basebandBwHz: 2_500_000, biasTee: true, samplesPerBlock: 2);

        var blocks = src.Blocks().ToList();

        Assert.Equal(2, blocks.Count);

        Assert.Equal(2, blocks[0].SampleCount);
        Assert.Equal(0f, blocks[0].I[0], 3);
        Assert.Equal(64f / 128f, blocks[0].Q[0], 3);
        Assert.Equal(-1f, blocks[0].I[1], 3);          // (sbyte)128 = -128
        Assert.Equal(-64f / 128f, blocks[0].Q[1], 3);  // (sbyte)192 = -64

        Assert.Equal(127f / 128f, blocks[1].I[0], 3);
        Assert.Equal(-1f / 128f, blocks[1].Q[0], 3);   // (sbyte)255 = -1
        Assert.Equal(32f / 128f, blocks[1].I[1], 3);
        Assert.Equal(-32f / 128f, blocks[1].Q[1], 3);  // (sbyte)224 = -32

        Assert.True(device.Opened);
        Assert.True(device.Closed);

        // The configured RxGain/baseband bandwidth/bias-tee must reach the device untouched.
        Assert.Equal(gain, device.OpenedGain);
        Assert.Equal(2_500_000, device.OpenedBasebandBwHz);
        Assert.True(device.OpenedBiasTee);
    }

    // M8 — no hardware: graceful fallback, Blocks() yields nothing (offline-first, SPEC §8.1).
    [Fact]
    public void Blocks_YieldsNothing_WhenDeviceUnavailable()
    {
        var device = new FakeHackRfDevice(available: false, new byte[] { 1, 2, 3, 4 });
        var src = new HackRfSampleSource(
            device, 915_000_000, 2_000_000, RxGain.Default, basebandBwHz: 2_000_000, biasTee: false,
            samplesPerBlock: 2);

        Assert.Empty(src.Blocks());
        Assert.False(device.Opened);
    }

    // M0-T3 / §4.2 L1 — the HackRF abstraction exposes NO transmit-capable member (receive-only).
    [Fact]
    public void IHackRfDevice_ExposesNoTransmitMember()
    {
        var members = typeof(IHackRfDevice).GetMembers()
            .Select(m => m.Name.ToLowerInvariant());
        Assert.DoesNotContain(members, n => n.Contains("transmit") || n.Contains("send") || n.Contains("tx"));
    }
}
