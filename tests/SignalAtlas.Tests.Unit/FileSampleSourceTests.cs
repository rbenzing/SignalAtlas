using SignalAtlas.Collector;

namespace SignalAtlas.Tests.Unit;

public class FileSampleSourceTests
{
    // M0-T1 — deterministic replay: same bytes → identical ordered blocks.
    [Fact]
    public void Blocks_AreDeterministic_ForSameInput()
    {
        var raw = new byte[2 * 4 * 3]; // 3 blocks of 4 samples (interleaved I/Q)
        for (int n = 0; n < raw.Length; n++) raw[n] = (byte)(n * 7 % 251);
        var src = new FileSampleSource(raw, samplesPerBlock: 4, centerFreqHz: 915_000_000, sampleRateHz: 2_000_000);

        var first = src.Blocks().ToList();
        var second = src.Blocks().ToList();

        Assert.Equal(3, first.Count);
        Assert.Equal(first.Count, second.Count);
        for (int b = 0; b < first.Count; b++)
        {
            Assert.Equal(first[b].I, second[b].I);
            Assert.Equal(first[b].Q, second[b].Q);
        }
    }

    // M0-T1 — 8-bit interleaved bytes decode to normalized [-1,1) I/Q in order.
    [Fact]
    public void Blocks_DecodeInterleavedSignedBytes_ToNormalizedIq()
    {
        var raw = new byte[] { 0, 64, 128, 192 }; // I0=0,Q0=64,I1=-128,Q1=-64 (signed)
        var src = new FileSampleSource(raw, samplesPerBlock: 2, centerFreqHz: 100, sampleRateHz: 4);

        var block = Assert.Single(src.Blocks());

        Assert.Equal(2, block.SampleCount);
        Assert.Equal(0f, block.I[0], 3);
        Assert.Equal(64f / 128f, block.Q[0], 3);
        Assert.Equal(-1f, block.I[1], 3);          // (sbyte)128 = -128
        Assert.Equal(-64f / 128f, block.Q[1], 3);  // (sbyte)192 = -64
    }

    // M0-T1 — a trailing partial block is dropped (deterministic full blocks only).
    [Fact]
    public void Blocks_DropTrailingPartialBlock()
    {
        var raw = new byte[2 * 5]; // 5 samples, block size 2 → 2 full blocks, 1 sample dropped
        var src = new FileSampleSource(raw, samplesPerBlock: 2, centerFreqHz: 1, sampleRateHz: 1);

        Assert.Equal(2, src.Blocks().Count());
    }
}
