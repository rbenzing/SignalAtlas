using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>Deterministic clock: returns a fixed UTC instant and time source.</summary>
internal sealed class FixedClock(DateTimeOffset now, TimeSource source = TimeSource.Gps) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
    public TimeSource Source { get; } = source;
}

/// <summary>Position source that always returns the same fix (or null).</summary>
internal sealed class StaticPositionSource(Position? position) : IPositionSource
{
    public Position? Current { get; } = position;
}

/// <summary>
/// A sample source that is ALSO transmit-capable (as real SDR hardware is). Records any
/// transmit invocation so the receive-only invariant (SPEC §4.2 L1) can be asserted.
/// </summary>
internal sealed class TransmitSpySampleSource(IEnumerable<IqBlock> blocks) : ISampleSource, ITransmitCapable
{
    private readonly IEnumerable<IqBlock> _blocks = blocks;
    public int TransmitCallCount { get; private set; }

    public IEnumerable<IqBlock> Blocks() => _blocks;
    public void Transmit(ReadOnlyMemory<byte> payload) => TransmitCallCount++;
}
