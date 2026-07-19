namespace SignalAtlas.Domain;

/// <summary>Read access to raised anomaly alerts (SPEC §9.2 GET /alerts).</summary>
public interface IAlertRepository
{
    IReadOnlyList<Alert> GetAlerts(int limit = 100);

    /// <summary>Total alert count (#8 — avoids a full-table read just to count rows). Additive: the
    /// default derives from <see cref="GetAlerts"/> so existing implementers keep compiling; the real
    /// EF/in-memory stores override it with an efficient, provider/lock-appropriate count.</summary>
    int Count() => GetAlerts(int.MaxValue).Count;
}

/// <summary>Append access to raised anomaly alerts (SPEC §8.8) — the live pipeline persists what it raises.</summary>
public interface IAlertWriter
{
    void Add(Alert a);
}
