using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>Offline-first in-memory session store (SPEC §7.9, §4.3). Empty by default; sessions are recorded live.</summary>
public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public void Add(Session session) => _sessions[session.Id] = session;

    public IReadOnlyList<Session> All() =>
        _sessions.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

    public Session? Get(string id) => _sessions.GetValueOrDefault(id);
}

/// <summary>Offline-first in-memory analysis-run store (SPEC §7.9). Tracks status transitions in place.</summary>
public sealed class InMemoryAnalysisRunRepository : IAnalysisRunRepository
{
    private readonly Dictionary<Guid, AnalysisRun> _runs = new();

    public void Add(AnalysisRun run) => _runs[run.Id] = run;

    public void Update(AnalysisRun run) => _runs[run.Id] = run;

    public AnalysisRun? Get(Guid id) => _runs.GetValueOrDefault(id);

    public IReadOnlyList<AnalysisRun> All() => _runs.Values.OrderBy(r => r.Id).ToList();
}

/// <summary>
/// Offline-first in-memory enrichment store (SPEC §7.9). <see cref="Add"/> enforces the non-empty
/// citation guard (AC-DA1): the portable belt to the Postgres <c>enrichment_cited</c> CHECK.
/// </summary>
public sealed class InMemoryEnrichmentRepository : IEnrichmentRepository
{
    private readonly Dictionary<Guid, Enrichment> _enrichments = new();

    public void Add(Enrichment enrichment)
    {
        EnrichmentGuard.RequireCitations(enrichment);
        _enrichments[enrichment.Id] = enrichment;
    }

    public Enrichment? Get(Guid id) => _enrichments.GetValueOrDefault(id);

    public IReadOnlyList<Enrichment> Query(Guid? runId, string? targetId) =>
        _enrichments.Values
            .Where(e => (runId is null || e.RunId == runId) && (targetId is null || e.TargetId == targetId))
            .OrderBy(e => e.Created)
            .ToList();

    public void SetStatus(Guid id, string status)
    {
        if (_enrichments.TryGetValue(id, out var e))
            _enrichments[id] = e with { Status = status };
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
