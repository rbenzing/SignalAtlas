namespace SignalAtlas.Domain;

/// <summary>Read access to raised anomaly alerts (SPEC §9.2 GET /alerts).</summary>
public interface IAlertRepository
{
    IReadOnlyList<Alert> GetAlerts(int limit = 100);

    /// <summary>Total alert count (#8 — avoids a full-table read just to count rows). Additive: the
    /// default derives from <see cref="GetAlerts"/> so existing implementers keep compiling; the real
    /// EF/in-memory stores override it with an efficient, provider/lock-appropriate count.</summary>
    int Count() => GetAlerts(int.MaxValue).Count;

    /// <summary>Alerts at or after <paramref name="since"/>, most-recent-first, matching
    /// <see cref="GetAlerts"/>'s ordering (#8 — a windowed query, e.g. the analyst's "what changed",
    /// instead of pulling the whole alerts table and filtering client-side). Additive: the default
    /// filters <see cref="GetAlerts"/>'s full result; the EF store overrides it with a targeted query.</summary>
    IReadOnlyList<Alert> GetSince(DateTimeOffset since) =>
        GetAlerts(int.MaxValue).Where(a => a.Time >= since).ToList();
}

/// <summary>Append access to raised anomaly alerts (SPEC §8.8) — the live pipeline persists what it raises.</summary>
public interface IAlertWriter
{
    void Add(Alert a);
}
