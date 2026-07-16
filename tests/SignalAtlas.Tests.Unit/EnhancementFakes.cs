using SignalAtlas.Analyst;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>List-backed fakes for the deferred-analyzer tests (SPEC §8.13). No DB, no network.</summary>
internal sealed class FakeAnalysisRunRepository : IAnalysisRunRepository
{
    private readonly Dictionary<Guid, AnalysisRun> _runs = new();
    public void Add(AnalysisRun run) => _runs[run.Id] = run;
    public void Update(AnalysisRun run) => _runs[run.Id] = run;
    public AnalysisRun? Get(Guid id) => _runs.GetValueOrDefault(id);
    public IReadOnlyList<AnalysisRun> All() => _runs.Values.ToList();
}

internal sealed class FakeEnrichmentRepository : IEnrichmentRepository
{
    public List<Enrichment> Added { get; } = [];

    public void Add(Enrichment enrichment)
    {
        // Mirror the app-level non-empty-citation guard (AC-DA1) so the analyzer path is exercised faithfully.
        if (enrichment.Citations.Count == 0)
            throw new InvalidOperationException("Enrichment must be cited (AC-DA1).");
        Added.Add(enrichment);
    }

    public Enrichment? Get(Guid id) => Added.FirstOrDefault(e => e.Id == id);

    public IReadOnlyList<Enrichment> Query(Guid? runId, string? targetId) =>
        Added.Where(e => (runId is null || e.RunId == runId) && (targetId is null || e.TargetId == targetId)).ToList();

    public void SetStatus(Guid id, string status)
    {
        var i = Added.FindIndex(e => e.Id == id);
        if (i >= 0) Added[i] = Added[i] with { Status = status };
    }
}

/// <summary>Flippable connectivity so the offline→reconnect queue path (AC-DA5) is testable.</summary>
internal sealed class MutableConnectivity(bool online) : IConnectivity
{
    public bool IsOnline { get; set; } = online;
}
