using SignalAtlas.Domain;

namespace SignalAtlas.Api;

/// <summary>
/// Rate-limits the live <see cref="SpectrumFrame"/> broadcast (leading-edge throttle) so a continuous
/// IQ stream — e.g. browser WebUSB at the block rate (~244 frames/s at 2 MS/s ÷ 8192) — does not flood
/// SignalR clients and freeze the browser main thread with JSON deserialization of 4096-bin PSDs plus
/// full waterfall redraws. Frames arriving faster than <c>minInterval</c> are coalesced (dropped); the
/// next kept frame supersedes them visually, which is exactly right for a live waterfall.
///
/// Only <see cref="SpectrumFrame"/> is throttled; every other live event passes straight through so
/// signals/emitters/alerts/devices are never delayed or dropped. This decorates ONLY the fire-and-forget
/// live push — the deterministic pipeline result, persisted artifacts, and the REST spectrum buffer are
/// untouched (they still see every frame). One instance is created per WebSocket connection and is only
/// ever called from that connection's single pipeline thread, so no locking is required.
/// </summary>
public sealed class ThrottledSpectrumNotifier : ILiveNotifier
{
    private readonly ILiveNotifier _inner;
    private readonly IClock _clock;
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _lastPush = DateTimeOffset.MinValue;

    public ThrottledSpectrumNotifier(ILiveNotifier inner, IClock clock, TimeSpan minInterval)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _minInterval = minInterval;
    }

    public void SpectrumFrame(SpectrumFrame frame)
    {
        var now = _clock.UtcNow;
        if (now - _lastPush < _minInterval)
            return; // coalesce: faster than the display cadence — the next kept frame wins.
        _lastPush = now;
        _inner.SpectrumFrame(frame);
    }

    public void SignalCreated(Signal signal) => _inner.SignalCreated(signal);
    public void EmitterUpdated(Emitter emitter) => _inner.EmitterUpdated(emitter);
    public void AlertRaised(Alert alert) => _inner.AlertRaised(alert);
    public void DeviceDetermined(Device device) => _inner.DeviceDetermined(device);
}
