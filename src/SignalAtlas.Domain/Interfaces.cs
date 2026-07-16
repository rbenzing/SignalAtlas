namespace SignalAtlas.Domain;

/// <summary>
/// Receive-only source of IQ sample blocks. The abstraction deliberately exposes
/// no transmit member — the receive-only invariant (SPEC §4.2 L1) is enforced at the
/// type level, and a spy verifies no transmit-capable path is invoked (M0-T3).
/// </summary>
public interface ISampleSource
{
    IEnumerable<IqBlock> Blocks();
}

/// <summary>
/// Capability that real SDR hardware physically possesses but Signal Atlas software
/// must NEVER invoke (SPEC §4.2 L1). Present only so the receive-only invariant is
/// testable: a spy implements it and asserts it is never called during collection.
/// </summary>
public interface ITransmitCapable
{
    void Transmit(ReadOnlyMemory<byte> payload);
}

/// <summary>Current position, or null when no fix is available (SPEC §8.1, G13).</summary>
public interface IPositionSource
{
    Position? Current { get; }
}

/// <summary>UTC clock with an explicit source (SPEC §5.4, G14).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    TimeSource Source { get; }
}
