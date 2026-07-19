using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>Offline-first in-memory session store (SPEC §7.9, §4.3). Empty by default; sessions are
/// recorded live. Thread-safe (#10): a singleton hit by concurrent POST/GET handlers, guarded with a
/// lock like the other in-memory repos.</summary>
public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Add(Session session)
    {
        lock (_gate)
            _sessions[session.Id] = session;
    }

    public IReadOnlyList<Session> All()
    {
        lock (_gate)
            return _sessions.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    public Session? Get(string id)
    {
        lock (_gate)
            return _sessions.GetValueOrDefault(id);
    }
}

/// <summary>Offline-first in-memory analysis-run store (SPEC §7.9). Tracks status transitions in
/// place. Thread-safe (#10): a singleton hit by concurrent POST/GET handlers, guarded with a lock
/// like the other in-memory repos.</summary>
public sealed class InMemoryAnalysisRunRepository : IAnalysisRunRepository
{
    private readonly Dictionary<Guid, AnalysisRun> _runs = new();
    private readonly object _gate = new();

    public void Add(AnalysisRun run)
    {
        lock (_gate)
            _runs[run.Id] = run;
    }

    public void Update(AnalysisRun run)
    {
        lock (_gate)
            _runs[run.Id] = run;
    }

    public AnalysisRun? Get(Guid id)
    {
        lock (_gate)
            return _runs.GetValueOrDefault(id);
    }

    public IReadOnlyList<AnalysisRun> All()
    {
        lock (_gate)
            return _runs.Values.OrderBy(r => r.Id).ToList();
    }
}

/// <summary>
/// Offline-first in-memory enrichment store (SPEC §7.9). <see cref="Add"/> enforces the non-empty
/// citation guard (AC-DA1): the portable belt to the Postgres <c>enrichment_cited</c> CHECK.
/// Thread-safe (#10): a singleton hit by concurrent POST/GET handlers, guarded with a lock like the
/// other in-memory repos.
/// </summary>
public sealed class InMemoryEnrichmentRepository : IEnrichmentRepository
{
    private readonly Dictionary<Guid, Enrichment> _enrichments = new();
    private readonly object _gate = new();

    public void Add(Enrichment enrichment)
    {
        EnrichmentGuard.RequireCitations(enrichment);
        lock (_gate)
            _enrichments[enrichment.Id] = enrichment;
    }

    public Enrichment? Get(Guid id)
    {
        lock (_gate)
            return _enrichments.GetValueOrDefault(id);
    }

    public IReadOnlyList<Enrichment> Query(Guid? runId, string? targetId)
    {
        lock (_gate)
            return _enrichments.Values
                .Where(e => (runId is null || e.RunId == runId) && (targetId is null || e.TargetId == targetId))
                .OrderBy(e => e.Created)
                .ToList();
    }

    public void SetStatus(Guid id, string status)
    {
        lock (_gate)
        {
            if (_enrichments.TryGetValue(id, out var e))
                _enrichments[id] = e with { Status = status };
        }
    }
}

/// <summary>
/// The application-level non-empty-citation guard (SPEC §7.9 <c>enrichment_cited</c>, AC-DA1). SQLite
/// can't easily CHECK <c>jsonb_array_length</c> on JSON-as-text, so BOTH repository impls enforce it in
/// <c>Add</c>; the Postgres CHECK is the belt-and-suspenders that mirrors it at the DB layer.
/// </summary>
internal static class EnrichmentGuard
{
    public static void RequireCitations(Enrichment enrichment)
    {
        if (enrichment.Citations is null || enrichment.Citations.Count == 0)
            throw new InvalidOperationException(
                $"Enrichment {enrichment.Id} has no citations; every enrichment must be grounded (SPEC §7.9 enrichment_cited, AC-DA1).");
    }
}
