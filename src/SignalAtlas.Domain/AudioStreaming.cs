namespace SignalAtlas.Domain;

/// <summary>
/// RF Audio Player tap seam (design §2/§3): <see cref="Pipeline.IngestionPipeline"/> (via the
/// <c>SignalAtlas.Pipeline</c> project) depends only on this tiny interface, never on the concrete
/// fan-out hub (which lives in <c>SignalAtlas.Api</c>, alongside the WebSocket endpoint it serves).
/// A single shared implementation is a SINGLETON: it demodulates each accepted block with whatever
/// audio mode is currently selected and fans the PCM out to connected `/audio` clients. Implementations
/// MUST be a no-op (zero cost) when disabled or when no client is listening -- this is called on
/// every IQ block of every live pipeline run.
/// </summary>
public interface IAudioSink
{
    /// <summary>Offer one IQ block for audio demodulation/fan-out. Receive-only (L1): reads the
    /// block, never transmits. Never persists audio (transient stream only).</summary>
    void Accept(IqBlock block);
}
