namespace SignalAtlas.Domain;

/// <summary>
/// Real-time push seam (SPEC §9.3 <c>/hub/live</c>): the edge pipeline calls these as it produces
/// each artifact so live clients see signals/emitters/alerts/frames/devices without polling. Payloads
/// match the REST schemas (camelCase over the wire). Calls are FIRE-AND-FORGET — an implementation
/// must never block or throw into the pipeline hot loop (SPEC §4.9 backpressure discipline).
///
/// The default <see cref="NullLiveNotifier"/> is a no-op so pipeline unit tests stay dependency-free;
/// the SignalR-backed implementation lives in the API composition root.
/// </summary>
public interface ILiveNotifier
{
    void SignalCreated(Signal signal);
    void EmitterUpdated(Emitter emitter);
    void AlertRaised(Alert alert);
    void SpectrumFrame(SpectrumFrame frame);
    void DeviceDetermined(Device device);
}

/// <summary>No-op <see cref="ILiveNotifier"/> — the default when no live transport is wired.</summary>
public sealed class NullLiveNotifier : ILiveNotifier
{
    public static readonly NullLiveNotifier Instance = new();

    public void SignalCreated(Signal signal) { }
    public void EmitterUpdated(Emitter emitter) { }
    public void AlertRaised(Alert alert) { }
    public void SpectrumFrame(SpectrumFrame frame) { }
    public void DeviceDetermined(Device device) { }
}
