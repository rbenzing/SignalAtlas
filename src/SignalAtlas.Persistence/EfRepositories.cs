using Microsoft.EntityFrameworkCore;
using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

// PORTABILITY NOTE: SQLite cannot ORDER BY a DateTimeOffset (stored as TEXT), while Postgres can.
// To keep ONE code path that runs on both providers (SPEC §4.3), each limited/ordered read below
// splits on db.Database.IsNpgsql(): on Npgsql the ORDER BY + Take/Where run server-side (fixing the
// full-table-scan at real volume, #8); on SQLite the same query still materializes then orders/filters
// client-side, exactly as before, so the SQLite test fixtures keep passing.

/// <summary>EF-backed observation store (SPEC §7.2). Writes append; reads return the most recent.</summary>
public sealed class EfObservationRepository(SignalAtlasDbContext db) : IObservationRepository
{
    public void Add(Observation o)
    {
        db.Observations.Add(o);
        db.SaveChanges();
    }

    public IReadOnlyList<Observation> GetRecent(int limit) =>
        db.Database.IsNpgsql()
            ? db.Observations.OrderByDescending(o => o.Time).Take(limit).ToList()
            : db.Observations.AsEnumerable().OrderByDescending(o => o.Time).Take(limit).ToList();
}

/// <summary>EF-backed signal store (SPEC §9.2 GET /signals). Reads recent; the pipeline appends.</summary>
public sealed class EfSignalRepository(SignalAtlasDbContext db) : ISignalRepository, ISignalWriter
{
    public IReadOnlyList<Signal> GetSignals(int limit = 100) =>
        db.Database.IsNpgsql()
            ? db.Signals.OrderByDescending(s => s.Time).Take(limit).ToList()
            : db.Signals.AsEnumerable().OrderByDescending(s => s.Time).Take(limit).ToList();

    /// <summary>Total signal row count — server-side (SPEC §9.2 pagination / dashboards, #8).</summary>
    public int Count() => db.Signals.Count();

    /// <summary>Signals at or after <paramref name="since"/>, most-recent-first (#8).</summary>
    public IReadOnlyList<Signal> GetSince(DateTimeOffset since) =>
        db.Database.IsNpgsql()
            ? db.Signals.Where(s => s.Time >= since).OrderByDescending(s => s.Time).ToList()
            : db.Signals.AsEnumerable().Where(s => s.Time >= since).OrderByDescending(s => s.Time).ToList();

    /// <summary>Appends a classified signal from the live ingestion pipeline (SPEC §4.10).</summary>
    public void Add(Signal s)
    {
        db.Signals.Add(s);
        db.SaveChanges();
    }
}

/// <summary>EF-backed device store (SPEC §9.2 GET /devices; §8.4 write from the live decode stage).</summary>
public sealed class EfDeviceRepository(SignalAtlasDbContext db) : IDeviceRepository
{
    public IReadOnlyList<Device> GetDevices(int limit = 100) =>
        db.Devices.OrderBy(d => d.Id).Take(limit).ToList();

    /// <summary>Total device row count — server-side (#8).</summary>
    public int Count() => db.Devices.Count();

    /// <summary>A single device by id via a keyed lookup — server-side, no table scan (#8).</summary>
    public Device? Get(string id) => db.Devices.Find(id);

    /// <summary>Idempotent insert-or-update keyed on the deterministic device id (SPEC §8.4). Merges
    /// into an existing row (see <see cref="DeviceMerge"/>) so identity accumulates across blocks
    /// instead of a later frame silently erasing an earlier one's identifiers/evidence.
    /// RACE HARDENING (#16): this is check-then-act (Find, then Add-or-Update), so a concurrent
    /// upsert of the SAME deterministic id can insert between our Find and SaveChanges and turn our
    /// Add into a duplicate-key failure. On that failure, reload the now-committed row and apply the
    /// merge once instead of crashing; a genuinely repeated failure still surfaces.</summary>
    public void Upsert(Device device)
    {
        var existing = db.Devices.Find(device.Id);
        if (existing is null)
        {
            db.Devices.Add(device);
            try
            {
                db.SaveChanges();
                return;
            }
            catch (DbUpdateException)
            {
                db.Entry(device).State = EntityState.Detached;
                existing = db.Devices.Find(device.Id);
                if (existing is null)
                    throw; // not a duplicate-key race after all — surface the original failure
            }
        }

        db.Entry(existing).CurrentValues.SetValues(DeviceMerge.Merge(existing, device));
        db.SaveChanges();
    }
}

/// <summary>EF-backed emitter store (SPEC §7.2 emitters, §8.5). Reads all; the pipeline upserts.</summary>
public sealed class EfEmitterRepository(SignalAtlasDbContext db) : IEmitterRepository
{
    public IReadOnlyList<Emitter> All() => db.Emitters.OrderBy(e => e.Id).ToList();

    /// <summary>
    /// Idempotent insert-or-update keyed on the deterministic emitter id (SPEC §7.8): correlation
    /// re-emits the same id for the same transmitter, so re-runs converge instead of duplicating.
    /// RACE HARDENING (#16): same check-then-act shape as <see cref="EfDeviceRepository.Upsert"/> —
    /// a concurrent insert of the same id between our Find and SaveChanges is caught and retried once
    /// against the now-committed row instead of crashing.
    /// </summary>
    public void Upsert(Emitter e)
    {
        var existing = db.Emitters.Find(e.Id);
        if (existing is null)
        {
            db.Emitters.Add(e);
            try
            {
                db.SaveChanges();
                return;
            }
            catch (DbUpdateException)
            {
                db.Entry(e).State = EntityState.Detached;
                existing = db.Emitters.Find(e.Id);
                if (existing is null)
                    throw; // not a duplicate-key race after all — surface the original failure
            }
        }

        db.Entry(existing).CurrentValues.SetValues(e);
        db.SaveChanges();
    }
}

/// <summary>EF-backed alert store (SPEC §9.2 GET /alerts; §8.8 append from the live pipeline).</summary>
public sealed class EfAlertRepository(SignalAtlasDbContext db) : IAlertRepository, IAlertWriter
{
    public IReadOnlyList<Alert> GetAlerts(int limit = 100) =>
        db.Database.IsNpgsql()
            ? db.Alerts.OrderByDescending(a => a.Time).Take(limit).ToList()
            : db.Alerts.AsEnumerable().OrderByDescending(a => a.Time).Take(limit).ToList();

    /// <summary>Total alert row count — server-side (#8).</summary>
    public int Count() => db.Alerts.Count();

    /// <summary>Alerts at or after <paramref name="since"/>, most-recent-first (#8) — server-side on
    /// Npgsql; SQLite materializes then orders client-side (PORTABILITY NOTE above).</summary>
    public IReadOnlyList<Alert> GetSince(DateTimeOffset since) =>
        db.Database.IsNpgsql()
            ? db.Alerts.Where(a => a.Time >= since).OrderByDescending(a => a.Time).ToList()
            : db.Alerts.AsEnumerable().Where(a => a.Time >= since).OrderByDescending(a => a.Time).ToList();

    /// <summary>Appends an anomaly alert raised by the live ingestion pipeline (SPEC §8.8).</summary>
    public void Add(Alert a)
    {
        db.Alerts.Add(a);
        db.SaveChanges();
    }
}

/// <summary>EF-backed session store (SPEC §7.9 sessions, §9.2 GET /sessions).</summary>
public sealed class EfSessionRepository(SignalAtlasDbContext db) : ISessionRepository
{
    public void Add(Session session)
    {
        db.Sessions.Add(session);
        db.SaveChanges();
    }

    public IReadOnlyList<Session> All() => db.Sessions.OrderBy(s => s.Id).ToList();

    public Session? Get(string id) => db.Sessions.Find(id);
}

/// <summary>EF-backed analysis-run store (SPEC §7.9 analysis_runs). Records status transitions.</summary>
public sealed class EfAnalysisRunRepository(SignalAtlasDbContext db) : IAnalysisRunRepository
{
    public void Add(AnalysisRun run)
    {
        db.AnalysisRuns.Add(run);
        db.SaveChanges();
    }

    public void Update(AnalysisRun run)
    {
        var existing = db.AnalysisRuns.Find(run.Id);
        if (existing is null)
            db.AnalysisRuns.Add(run);
        else
            db.Entry(existing).CurrentValues.SetValues(run);
        db.SaveChanges();
    }

    public AnalysisRun? Get(Guid id) => db.AnalysisRuns.Find(id);

    public IReadOnlyList<AnalysisRun> All() => db.AnalysisRuns.OrderBy(r => r.Id).ToList();
}

/// <summary>
/// EF-backed enrichment store (SPEC §7.9 enrichments). <see cref="Add"/> enforces the non-empty
/// citation guard (AC-DA1) at the app layer — the portable belt to the Postgres <c>enrichment_cited</c>
/// CHECK (jsonb can't be checked when citations are JSON-as-text on SQLite).
/// </summary>
public sealed class EfEnrichmentRepository(SignalAtlasDbContext db) : IEnrichmentRepository
{
    public void Add(Enrichment enrichment)
    {
        EnrichmentGuard.RequireCitations(enrichment);
        db.Enrichments.Add(enrichment);
        db.SaveChanges();
    }

    public Enrichment? Get(Guid id) => db.Enrichments.Find(id);

    public IReadOnlyList<Enrichment> Query(Guid? runId, string? targetId)
    {
        var filtered = db.Enrichments
            .Where(e => (runId == null || e.RunId == runId) && (targetId == null || e.TargetId == targetId));

        // Created is a DateTimeOffset too (see PORTABILITY NOTE); Npgsql can order it server-side,
        // SQLite still materializes first and orders client-side.
        return db.Database.IsNpgsql()
            ? filtered.OrderBy(e => e.Created).ToList()
            : filtered.AsEnumerable().OrderBy(e => e.Created).ToList();
    }

    public void SetStatus(Guid id, string status)
    {
        var existing = db.Enrichments.Find(id);
        if (existing is null) return;
        db.Entry(existing).CurrentValues.SetValues(existing with { Status = status });
        db.SaveChanges();
    }
}

/// <summary>EF-backed append-only audit log (SPEC §7.2 audit_log, §4.6 / NFR-S2).</summary>
public sealed class EfAuditLog(SignalAtlasDbContext db) : IAuditLog
{
    public void Record(string actor, string action, string query)
    {
        db.AuditLog.Add(new AuditEntry(0, DateTimeOffset.UtcNow, actor, action, query));
        db.SaveChanges();
    }

    // Order by Id (monotonic insertion order) so equal timestamps stay deterministic.
    public IReadOnlyList<AuditEntry> Recent(int limit) =>
        db.AuditLog.OrderByDescending(a => a.Id).Take(limit).ToList();
}
