namespace SignalAtlas.Domain;

/// <summary>Read access to stored signals (SPEC §9.2 GET /signals).</summary>
public interface ISignalRepository
{
    IReadOnlyList<Signal> GetSignals(int limit = 100);

    /// <summary>Total signal count (#8 — avoids a full-table read just to count rows). Additive: the
    /// default derives from <see cref="GetSignals"/> so existing implementers keep compiling; the real
    /// EF/in-memory stores override it with an efficient, provider/lock-appropriate count.</summary>
    int Count() => GetSignals(int.MaxValue).Count;

    /// <summary>Signals at or after <paramref name="since"/>, most-recent-first, matching
    /// <see cref="GetSignals"/>'s ordering (#8). Additive: the default filters
    /// <see cref="GetSignals"/>'s full result; the real stores override it with a targeted query.</summary>
    IReadOnlyList<Signal> GetSince(DateTimeOffset since) =>
        GetSignals(int.MaxValue).Where(s => s.Time >= since).ToList();
}
