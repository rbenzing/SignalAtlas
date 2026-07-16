using SignalAtlas.Collector;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class ReceiveOnlyTests
{
    // M0-T3 — the transmit path is never initialized during collection (SPEC §4.2 L1).
    [Fact]
    public void Collect_NeverInvokesTransmit()
    {
        var blocks = new SyntheticSampleSource(4, 8, 915_000_000, 2_000_000).Blocks().ToList();
        var spy = new TransmitSpySampleSource(blocks);
        var collector = new ScanCollector("collector-A",
            new FixedClock(DateTimeOffset.UnixEpoch), new StaticPositionSource(null));

        _ = collector.Collect(spy).ToList();

        Assert.Equal(0, spy.TransmitCallCount);
    }

    // M0-T3 — the receive-only sample-source abstraction exposes no transmit member.
    [Fact]
    public void ISampleSource_ExposesNoTransmitMember()
    {
        var members = typeof(ISampleSource).GetMembers()
            .Select(m => m.Name.ToLowerInvariant());
        Assert.DoesNotContain(members, n => n.Contains("transmit") || n.Contains("send") || n.Contains("tx"));
    }
}
