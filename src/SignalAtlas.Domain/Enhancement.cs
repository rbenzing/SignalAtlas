namespace SignalAtlas.Domain;

/// <summary>
/// A field collection recording (SPEC §7.9 sessions, §4.10). The operator walks/drives a session and
/// the edge pipeline produces the authoritative result live; a session can LATER be optionally
/// enhanced by the Claude pass (§8.13). <see cref="ScanPlanJson"/> / <see cref="Bbox"/> are stored as
/// JSON text (portable across SQLite/Postgres, §4.3).
/// </summary>
public sealed record Session(
    string Id,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    string ScanPlanJson,
    string? Bbox,
    string? Notes);

/// <summary>
/// A deferred, operator-initiated Claude enhancement pass over a session/range (SPEC §7.9 analysis_runs,
/// §8.13). Never on the critical path (AC-DA0). <see cref="Status"/> transitions
/// queued → running → done | failed; offline requests are <see cref="Queued"/> (not failed) and execute
/// on reconnect (AC-DA5). <see cref="ReportJson"/> holds the grounded session report.
/// </summary>
public sealed record AnalysisRun(
    Guid Id,
    string? SessionId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string Engine,
    string Model,
    string Status,
    DateTimeOffset? Started,
    DateTimeOffset? Finished,
    string? ReportJson,
    long TokensUsed)
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    public const string ClaudeEngine = "claude";
}

/// <summary>
/// An attributed, advisory overlay proposal produced by an analysis run (SPEC §7.9 enrichments, §8.13).
/// It NEVER mutates the row it annotates (AC-DA2) — it overlays it with provenance (<see cref="RunId"/>).
/// <see cref="Citations"/> are non-empty (AC-DA1, DB-enforced): the exact records the proposal is
/// grounded on, reusing <see cref="EvidenceItem"/> as {feature = record-type, value = record-id}.
/// Advisory until the operator accepts/rejects (AC-DA4).
/// </summary>
public sealed record Enrichment(
    Guid Id,
    Guid RunId,
    string TargetEntity,
    string TargetId,
    string Kind,
    string ProposalJson,
    IReadOnlyList<EvidenceItem> Citations,
    string Status,
    DateTimeOffset Created)
{
    public const string Proposed = "proposed";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public const string Reclassification = "reclassification";
    public const string DeviceDetermination = "device_determination";
    public const string Narrative = "narrative";
    public const string LocationRefinement = "location_refinement";
    public const string Investigation = "investigation";
}

/// <summary>A request to start a deferred Claude enhancement pass (SPEC §8.13). Session OR time range.</summary>
public sealed record AnalysisRequest(
    string? SessionId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string Model);

/// <summary>
/// The optional, operator-chosen batch enhancement pass (SPEC §8.13, ADR-10). Runs after the fact, or
/// never — the platform is fully functional with this engine disabled (AC-DA0). Targets the residual
/// hard cases (Unknown/low-confidence), writes attributed, cited <see cref="Enrichment"/>s that never
/// overwrite edge results, and stores a grounded session report on the <see cref="AnalysisRun"/>.
/// </summary>
public interface IDeferredAnalyzer
{
    AnalysisRun Run(AnalysisRequest req);
}

/// <summary>Read/write access to collection sessions (SPEC §7.9, §9.2 GET /sessions).</summary>
public interface ISessionRepository
{
    void Add(Session session);
    IReadOnlyList<Session> All();
    Session? Get(string id);
}

/// <summary>Read/write access to analysis runs (SPEC §7.9, §9.2 /analysis/runs).</summary>
public interface IAnalysisRunRepository
{
    void Add(AnalysisRun run);
    void Update(AnalysisRun run);
    AnalysisRun? Get(Guid id);
    IReadOnlyList<AnalysisRun> All();
}

/// <summary>
/// Read/write access to enrichments (SPEC §7.9, §9.2 /enrichments). <see cref="Add"/> enforces the
/// non-empty-citation guard (AC-DA1) at the application layer — throwing on empty citations — which is
/// the portable belt to the Postgres <c>enrichment_cited</c> CHECK's suspenders (§7.9).
/// </summary>
public interface IEnrichmentRepository
{
    void Add(Enrichment enrichment);
    Enrichment? Get(Guid id);
    IReadOnlyList<Enrichment> Query(Guid? runId, string? targetId);
    void SetStatus(Guid id, string status);
}
