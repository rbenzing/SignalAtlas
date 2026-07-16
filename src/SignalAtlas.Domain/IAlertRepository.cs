namespace SignalAtlas.Domain;

/// <summary>Read access to raised anomaly alerts (SPEC §9.2 GET /alerts).</summary>
public interface IAlertRepository
{
    IReadOnlyList<Alert> GetAlerts(int limit = 100);
}

/// <summary>Append access to raised anomaly alerts (SPEC §8.8) — the live pipeline persists what it raises.</summary>
public interface IAlertWriter
{
    void Add(Alert a);
}
