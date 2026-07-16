using SignalAtlas.Collector;
using SignalAtlas.Domain;
using SignalAtlas.Pipeline;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// Bounded-queue backpressure with drop-oldest + counted metric (SPEC §4.9 G27, NFR-T1/NFR-C3).
/// Deterministic: producing more than the queue depth BEFORE draining guarantees drops without any
/// timing dependence.
/// </summary>
public class BoundedBackpressureTests
{
    // A block tagged by its center frequency so we can prove WHICH blocks survived a drop.
    private static IqBlock Block(long tag) => new(tag, 2_000_000, [0f], [0f]);

    [Fact]
    public void OverCapacity_DropsOldest_AndCountsDrops()
    {
        var buffer = new BoundedBlockBuffer(capacity: 2);

        for (long i = 1; i <= 5; i++)
            Assert.True(buffer.TryWrite(Block(i))); // drop-oldest always accepts.
        buffer.Complete();

        var drained = new List<IqBlock>();
        while (buffer.Reader.TryRead(out var b))
            drained.Add(b);

        Assert.Equal(2, drained.Count);          // never exceeds capacity (bounded growth).
        Assert.Equal(3, buffer.Drops);           // 5 written − 2 kept = 3 dropped, counted (never silent).
        Assert.Equal([4L, 5L], drained.Select(b => b.CenterFreqHz)); // NEWEST survive (drop-oldest).
    }

    [Fact]
    public void WithinCapacity_DropsNothing()
    {
        var buffer = new BoundedBlockBuffer(capacity: 5);

        for (long i = 1; i <= 3; i++)
            buffer.TryWrite(Block(i));
        buffer.Complete();

        var count = 0;
        while (buffer.Reader.TryRead(out _)) count++;

        Assert.Equal(3, count);
        Assert.Equal(0, buffer.Drops);
    }

    [Fact]
    public void BoundedSampleSource_PassesAllThrough_WhenCapacitySuffices()
    {
        var inner = new SyntheticSampleSource(6, 8, 915_000_000, 2_000_000);
        var bounded = new BoundedSampleSource(inner, capacity: 8);

        var blocks = bounded.Blocks().ToList();

        Assert.Equal(6, blocks.Count);
        Assert.Equal(0, bounded.Drops);
        Assert.Equal(8, bounded.Capacity);
    }
}
