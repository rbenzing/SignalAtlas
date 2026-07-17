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
    public void Enqueue_DiscardsIncompletePartialBlockOnComplete()
    {
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 2);
        // 3 bytes = 1.5 samples: not even one full 2-sample block → nothing enqueued.
        source.Enqueue(new byte[] { 1, 2, 3 }, 100, 1000);
        source.Complete();

        Assert.Empty(source.Blocks());
    }

    [Fact]
    public void Enqueue_CarriesRemainderAcrossCalls()
    {
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 2);
        // First call: 3 bytes = 1.5 samples, not a full 2-sample block → 3 bytes carried over.
        source.Enqueue(new byte[] { 1, 2, 3 }, 100, 1000);
        // Second call: 1 more byte completes the carried-over block (4 bytes = one 2-sample block).
        source.Enqueue(new byte[] { 4 }, 100, 1000);
        source.Complete();

        var block = Assert.Single(source.Blocks());
        Assert.Equal(2, block.SampleCount);
        Assert.Equal(1 / 128f, block.I[0], 5);
        Assert.Equal(2 / 128f, block.Q[0], 5);
        Assert.Equal(3 / 128f, block.I[1], 5);
        Assert.Equal(4 / 128f, block.Q[1], 5);
    }

    [Fact]
    public void Enqueue_DropsOldestBlocksWhenCapacityExceeded()
    {
        var source = new BrowserUploadSampleSource(capacity: 2, samplesPerBlock: 1);
        source.Enqueue(new byte[] { 10, 0 }, 100, 1000);
        source.Enqueue(new byte[] { 20, 0 }, 100, 1000);
        source.Enqueue(new byte[] { 30, 0 }, 100, 1000);
        source.Enqueue(new byte[] { 40, 0 }, 100, 1000);
        source.Complete();

        var blocks = source.Blocks().ToList();

        Assert.Equal(2, blocks.Count);
        Assert.Equal(30 / 128f, blocks[0].I[0], 5);
        Assert.Equal(40 / 128f, blocks[1].I[0], 5);
    }

    [Fact]
    public async Task Blocks_WaitsOnEmptyChannelThenYieldsLateArrivingData()
    {
        // The live path: the pipeline starts draining Blocks() BEFORE the browser has streamed any
        // IQ, so WaitToReadAsync() returns a NOT-yet-completed ValueTask. Blocking on it must wait,
        // not throw. (Regression guard: ValueTask.GetAwaiter().GetResult() on an incomplete op throws
        // "The asynchronous operation has not completed." — the wait must go through .AsTask().)
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 1);

        // Start draining while the channel is empty — Blocks() parks in the wait.
        var drained = Task.Run(() => source.Blocks().ToList());
        await Task.Delay(100); // ensure the drainer reached the empty-channel wait before we enqueue

        source.Enqueue(new byte[] { 42, 0 }, 100, 1000);
        source.Complete();

        var finished = await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(drained, finished); // did not hang
        var blocks = await drained;      // and did not throw
        Assert.Single(blocks);
        Assert.Equal(42 / 128f, blocks[0].I[0], 5);
    }

    [Fact]
    public void ExposesNoTransmitMember()
    {
        var members = typeof(BrowserUploadSampleSource).GetMembers()
            .Select(m => m.Name.ToLowerInvariant());
        Assert.DoesNotContain(members, n => n.Contains("transmit") || n.Contains("send") || n.Contains("tx"));
    }
}
