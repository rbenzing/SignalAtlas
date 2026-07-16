using SignalAtlas.Domain;
using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

/// <summary>
/// Unit tests for the bounded spectrum ring (SPEC §8.2 waterfall, NFR-C3 bounded-buffer discipline).
/// </summary>
public class SpectrumBufferTests
{
    private static SpectrumFrame Frame(int seed) => new(
        Time: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero).AddSeconds(seed),
        CenterFreqHz: 915_000_000,
        SampleRateHz: 2_000_000,
        PowerDbfs: [seed, seed, seed]);

    [Fact]
    public void Recent_ReturnsFramesNewestFirst()
    {
        var buffer = new InMemorySpectrumBuffer(capacity: 8);
        buffer.Push(Frame(1));
        buffer.Push(Frame(2));
        buffer.Push(Frame(3));

        var recent = buffer.Recent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal(3, recent[0].PowerDbfs[0]);
        Assert.Equal(2, recent[1].PowerDbfs[0]);
    }

    [Fact]
    public void Push_BeyondCapacity_DropsOldest_AndStaysBounded()
    {
        var buffer = new InMemorySpectrumBuffer(capacity: 4);
        for (int i = 1; i <= 10; i++)
            buffer.Push(Frame(i));

        var all = buffer.Recent(int.MaxValue);

        Assert.Equal(4, all.Count);              // never grows past capacity.
        Assert.Equal(10, all[0].PowerDbfs[0]);   // newest retained.
        Assert.Equal(7, all[3].PowerDbfs[0]);    // oldest surviving is frame 7 (1..6 dropped).
    }

    [Fact]
    public void Recent_ClampsToAvailable_WhenFewerFramesThanRequested()
    {
        var buffer = new InMemorySpectrumBuffer(capacity: 256);
        buffer.Push(Frame(1));

        Assert.Single(buffer.Recent(64));
    }
}
