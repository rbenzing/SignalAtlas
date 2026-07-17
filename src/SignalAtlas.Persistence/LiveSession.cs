namespace SignalAtlas.Persistence;

/// <summary>
/// An offline-first in-memory store seeded with demo data that can be cleared when a real device
/// starts streaming, so the UI shows live data only (never a mix of demo + live). Implemented only by
/// the in-memory repos + spectrum buffer; the EF (database) repos deliberately do NOT implement it, so
/// clearing is a no-op in DB mode and can never wipe a real database.
/// </summary>
public interface IDemoSeedStore
{
    void ClearDemoSeed();
}

/// <summary>
/// Coordinates the one-time transition from demo (seeded) data to live-only when a device begins
/// streaming. The first stream clears every <see cref="IDemoSeedStore"/> exactly once; reconnects do
/// NOT re-clear, so live data survives a disconnect/reconnect. Demo data returns on a process restart.
/// </summary>
public interface ILiveSession
{
    void OnDeviceStreamStarted();
}

/// <inheritdoc cref="ILiveSession"/>
public sealed class LiveSession(IEnumerable<IDemoSeedStore> stores) : ILiveSession
{
    private int _cleared; // 0 = not yet cleared, 1 = cleared. Latched via Interlocked (thread-safe).

    public void OnDeviceStreamStarted()
    {
        // Exactly-once: the first caller flips 0→1 and clears; every later caller is a no-op.
        if (Interlocked.Exchange(ref _cleared, 1) != 0)
            return;
        foreach (var store in stores)
            store.ClearDemoSeed();
    }
}
