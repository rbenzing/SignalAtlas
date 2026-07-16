using SignalAtlas.Collector;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class BrowserUploadSampleSourceTests
{
    [Fact]
    public void Enqueue_ConvertsInt8InterleavedToNormalizedIqBlock()
    {
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 2);
        // Two samples: (I=127,Q=-128), (I=0,Q=64) as signed bytes.
        var bytes = new byte[] { 127, unchecked((byte)-128), 0, 64 };

        source.Enqueue(bytes, 915_000_000, 2_000_000);
        source.Complete();

        var block = Assert.Single(source.Blocks());
        Assert.Equal(915_000_000, block.CenterFreqHz);
        Assert.Equal(2_000_000, block.SampleRateHz);
        Assert.Equal(2, block.SampleCount);
        Assert.Equal(127 / 128f, block.I[0], 5);
        Assert.Equal(-128 / 128f, block.Q[0], 5);
        Assert.Equal(0f, block.I[1], 5);
        Assert.Equal(64 / 128f, block.Q[1], 5);
    }

    [Fact]
    public void Blocks_YieldsQueuedThenTerminatesOnComplete()
    {
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 1);
        source.Enqueue(new byte[] { 10, 20 }, 100, 1000);
        source.Enqueue(new byte[] { 30, 40 }, 100, 1000);
        source.Complete();

        var blocks = source.Blocks().ToList();

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Enqueue_DropsRemainderShorterThanOneBlock()
    {
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 2);
        // 3 bytes = 1.5 samples: not even one full 2-sample block → nothing enqueued.
        source.Enqueue(new byte[] { 1, 2, 3 }, 100, 1000);
        source.Complete();

        Assert.Empty(source.Blocks());
    }

    [Fact]
    public void ExposesNoTransmitMember()
    {
        var members = typeof(BrowserUploadSampleSource).GetMembers()
            .Select(m => m.Name.ToLowerInvariant());
        Assert.DoesNotContain(members, n => n.Contains("transmit") || n.Contains("send") || n.Contains("tx"));
    }
}
