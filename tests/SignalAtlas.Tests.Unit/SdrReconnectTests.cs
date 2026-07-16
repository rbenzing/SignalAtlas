using SignalAtlas.Collector;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// SDR USB disconnect → auto-reconnect + health event (SPEC §5.5 NFR-R1, AC-C3). Deterministic:
/// the backoff delay is injected as a no-op that only records the schedule, so nothing really sleeps.
/// </summary>
public class SdrReconnectTests
{
    private const long CenterHz = 915_000_000;
    private const int SampleRateHz = 2_000_000;

    private static byte[] Buf() => [0, 64, 128, 192]; // 2 interleaved-IQ samples.

    private sealed class RecordingHealthSink : ISdrHealthSink
    {
        public List<SdrHealthEvent> Events { get; } = [];
        public void Report(SdrHealthEvent e) => Events.Add(e);
    }

    /// <summary>Device driven by a script of per-ReadBlock behaviors (return bytes / throw / end).</summary>
    private sealed class ScriptedDevice(params Func<ScriptedDevice, ReadOnlyMemory<byte>>[] reads) : IHackRfDevice
    {
        private readonly Queue<Func<ScriptedDevice, ReadOnlyMemory<byte>>> _reads = new(reads);
        public bool IsAvailable { get; set; } = true;
        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }

        public void OpenReceive(long centerFreqHz, int sampleRateHz, double gainDb) => OpenCount++;
        public ReadOnlyMemory<byte> ReadBlock() =>
            _reads.Count > 0 ? _reads.Dequeue()(this) : ReadOnlyMemory<byte>.Empty;
        public void Close() => CloseCount++;
    }

    private static ReconnectingHackRfSampleSource NewSource(
        IHackRfDevice device, ISdrHealthSink sink, List<TimeSpan> delays) =>
        new(device, CenterHz, SampleRateHz, gainDb: 32.0, samplesPerBlock: 2,
            health: sink, clock: new FixedClock(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero)),
            delay: delays.Add);

    [Fact]
    public void FailsThenRecovers_BlocksResume_AndHealthEventsRecorded()
    {
        var sink = new RecordingHealthSink();
        var delays = new List<TimeSpan>();
        var device = new ScriptedDevice(
            d => Buf(),                             // block 1
            d => throw new IOException("USB drop"), // mid-stream disconnect
            d => Buf(),                             // block 2 after reconnect
            d => ReadOnlyMemory<byte>.Empty);       // end-of-stream

        var blocks = NewSource(device, sink, delays).Blocks().ToList();

        Assert.Equal(2, blocks.Count);
        Assert.Equal(2, device.OpenCount); // initial open + one reconnect open.
        Assert.Contains(sink.Events, e => e.State == SdrHealthState.Connected && e.Attempt == 0);
        Assert.Contains(sink.Events, e => e.State == SdrHealthState.Reconnecting && e.Attempt == 1);
        Assert.Contains(sink.Events, e => e.State == SdrHealthState.Connected && e.Attempt == 1);
        Assert.DoesNotContain(sink.Events, e => e.State == SdrHealthState.Failed);
        Assert.Single(delays); // exactly one backoff wait, no real sleeping.
        Assert.Equal(ReconnectingHackRfSampleSource.BaseDelay, delays[0]);
    }

    [Fact]
    public void NeverRecovers_StopsAfterMaxAttempts_AndEmitsFailed()
    {
        var sink = new RecordingHealthSink();
        var delays = new List<TimeSpan>();
        var device = new ScriptedDevice(
            d => Buf(),                                                     // block 1
            d => { d.IsAvailable = false; throw new IOException("gone"); }); // drop, hardware vanishes

        var blocks = NewSource(device, sink, delays).Blocks().ToList();

        Assert.Single(blocks); // only the pre-drop block survived (loses ≤ in-flight block).
        Assert.Equal(ReconnectingHackRfSampleSource.MaxAttempts,
            sink.Events.Count(e => e.State == SdrHealthState.Reconnecting));
        Assert.Contains(sink.Events, e => e.State == SdrHealthState.Failed
            && e.Attempt == ReconnectingHackRfSampleSource.MaxAttempts);
        Assert.Equal(ReconnectingHackRfSampleSource.MaxAttempts, delays.Count);
    }

    [Fact]
    public void BackoffSchedule_IsBoundedExponential()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), ReconnectingHackRfSampleSource.BackoffFor(1));
        Assert.Equal(TimeSpan.FromMilliseconds(500), ReconnectingHackRfSampleSource.BackoffFor(2));
        Assert.Equal(TimeSpan.FromSeconds(1), ReconnectingHackRfSampleSource.BackoffFor(3));
        Assert.Equal(TimeSpan.FromSeconds(2), ReconnectingHackRfSampleSource.BackoffFor(4));
        Assert.Equal(TimeSpan.FromSeconds(4), ReconnectingHackRfSampleSource.BackoffFor(5));
        // Clamped at MaxDelay — never grows unbounded.
        Assert.Equal(ReconnectingHackRfSampleSource.MaxDelay, ReconnectingHackRfSampleSource.BackoffFor(20));
    }

    [Fact]
    public void UnavailableDevice_YieldsNothing()
    {
        var device = new ScriptedDevice(d => Buf()) { IsAvailable = false };
        var blocks = NewSource(device, new RecordingHealthSink(), []).Blocks().ToList();
        Assert.Empty(blocks);
    }

    // Receive-only invariant (SPEC §4.2 L1): the resilience wrapper exposes no transmit member.
    [Fact]
    public void Wrapper_ExposesNoTransmitMember()
    {
        var members = typeof(ReconnectingHackRfSampleSource).GetMembers()
            .Select(m => m.Name.ToLowerInvariant());
        Assert.DoesNotContain(members, n => n.Contains("transmit") || n.Contains("send") || n.Contains("tx"));
    }
}
