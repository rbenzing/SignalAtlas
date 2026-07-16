namespace SignalAtlas.Domain;

/// <summary>Lifecycle state of the SDR receive link (SPEC §5.5 NFR-R1).</summary>
public enum SdrHealthState
{
    /// <summary>Link is up and delivering blocks.</summary>
    Connected,

    /// <summary>The link dropped; a bounded-backoff reconnect attempt is in progress.</summary>
    Reconnecting,

    /// <summary>Reconnect exhausted its attempt budget; the source stops yielding blocks.</summary>
    Failed
}

/// <summary>
/// A health transition for the SDR receive link (SPEC §5.5 NFR-R1). Emitted whenever the device
/// connects, begins a reconnect attempt, or gives up. Carries the reconnect <see cref="Attempt"/>
/// number (0 for the initial connect) and the wall-clock <see cref="Time"/> of the transition.
/// </summary>
public readonly record struct SdrHealthEvent(SdrHealthState State, int Attempt, DateTimeOffset Time);

/// <summary>
/// Sink for SDR health transitions (SPEC §5.5 NFR-R1). The resilience wrapper reports connect /
/// reconnect / failed events here so operators (and tests) observe USB drop→reconnect without
/// coupling the collector to any concrete logging/metrics backend.
/// </summary>
public interface ISdrHealthSink
{
    void Report(SdrHealthEvent e);
}
