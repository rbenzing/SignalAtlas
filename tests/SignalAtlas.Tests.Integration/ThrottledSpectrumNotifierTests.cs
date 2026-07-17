using SignalAtlas.Api;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Integration;

public class ThrottledSpectrumNotifierTests
{
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public TimeSource Source => TimeSource.Host;
    }

    private sealed class CountingNotifier : ILiveNotifier
    {
        public int Frames;
        public int Signals;
        public void SpectrumFrame(SpectrumFrame frame) => Frames++;
        public void SignalCreated(Signal signal) => Signals++;
        public void EmitterUpdated(Emitter emitter) { }
        public void AlertRaised(Alert alert) { }
        public void DeviceDetermined(Device device) { }
    }

    private static SpectrumFrame Frame(DateTimeOffset t) =>
        new(t, 915_000_000, 2_000_000, new double[] { -50.0 });

    [Fact]
    public void SpectrumFrame_ForwardsFirst_DropsWithinInterval_ForwardsAfterInterval()
    {
        var clock = new MutableClock();
        var inner = new CountingNotifier();
        var throttle = new ThrottledSpectrumNotifier(inner, clock, TimeSpan.FromMilliseconds(50));

        // First frame is always forwarded (no prior push).
        throttle.SpectrumFrame(Frame(clock.UtcNow));
        Assert.Equal(1, inner.Frames);

        // +20ms and +40ms are inside the 50ms window → coalesced (dropped).
        clock.UtcNow += TimeSpan.FromMilliseconds(20);
        throttle.SpectrumFrame(Frame(clock.UtcNow));
        clock.UtcNow += TimeSpan.FromMilliseconds(20);
        throttle.SpectrumFrame(Frame(clock.UtcNow));
        Assert.Equal(1, inner.Frames);

        // +60ms total crosses the window → forwarded again.
        clock.UtcNow += TimeSpan.FromMilliseconds(20);
        throttle.SpectrumFrame(Frame(clock.UtcNow));
        Assert.Equal(2, inner.Frames);
    }

    [Fact]
    public void NonSpectrumEvents_PassThroughUnthrottled()
    {
        var clock = new MutableClock();
        var inner = new CountingNotifier();
        var throttle = new ThrottledSpectrumNotifier(inner, clock, TimeSpan.FromMilliseconds(50));

        // No clock advance between calls — a throttle would collapse these, but only SpectrumFrame
        // is rate-limited; signal/emitter/alert/device events must always pass straight through.
        throttle.SignalCreated(null!);
        throttle.SignalCreated(null!);
        throttle.SignalCreated(null!);

        Assert.Equal(3, inner.Signals);
    }
}
