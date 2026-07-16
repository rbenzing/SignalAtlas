using System.Globalization;
using System.Text.Json;
using SignalAtlas.Analyst;
using SignalAtlas.Domain;

namespace SignalAtlas.Enhancement;

/// <summary>
/// The optional Claude enhancement pass (SPEC §8.13, ADR-10, AC-DA1/2/3/5). Targets the residual
/// Unknown/low-confidence signals, hands ONLY the structured egress payload to <see cref="IClaudeClient"/>
/// (StubClaudeClient in tests / offline), and writes <see cref="Enrichment"/>s that are:
/// <list type="bullet">
/// <item>cited — non-empty <see cref="Enrichment.Citations"/> grounded in the actual signal record (AC-DA1);</item>
/// <item>advisory — <see cref="Enrichment.Proposed"/> until accepted/rejected (AC-DA4);</item>
/// <item>overlay-only — the analyzer NEVER writes back to the signal/emitter store (AC-DA2).</item>
/// </list>
/// When offline the run is QUEUED (not failed) and executes on reconnect via <see cref="RunQueued"/> (AC-DA5).
/// The tool/egress layer is deterministic and Claude-free; the Claude call is the only non-deterministic step.
/// </summary>
public sealed class DeferredClaudeAnalyzer(
    ISignalRepository signals,
    IEmitterRepository emitters,
    IAnalysisRunRepository runs,
    IEnrichmentRepository enrichments,
    IClaudeClient claude,
    IConnectivity connectivity,
    IClock clock) : IDeferredAnalyzer
{
    private const string SystemPrompt =
        "You are Signal Atlas's optional data-enhancement analyst. Reason ONLY over the provided "
        + "structured RF records to reclassify residual Unknown/low-confidence signals. Cite them. Never fabricate.";

    private readonly ISignalRepository _signals = signals;
    private readonly IEmitterRepository _emitters = emitters;
    private readonly IAnalysisRunRepository _runs = runs;
    private readonly IEnrichmentRepository _enrichments = enrichments;
    private readonly IClaudeClient _claude = claude;
    private readonly IConnectivity _connectivity = connectivity;
    private readonly IClock _clock = clock;

    // Persistent-enough for the edge process lifetime: offline runs wait here until reconnect (AC-DA5).
    private readonly Queue<(Guid RunId, AnalysisRequest Request)> _queue = new();

    public AnalysisRun Run(AnalysisRequest req)
    {
        var runId = Guid.NewGuid();

        // AC-DA5: offline → the run is QUEUED (not failed) and executes on reconnect.
        if (!_connectivity.IsOnline)
        {
            var queued = new AnalysisRun(
                runId, req.SessionId, req.From, req.To,
                AnalysisRun.ClaudeEngine, req.Model, AnalysisRun.Queued,
                Started: null, Finished: null, ReportJson: null, TokensUsed: 0);
            _runs.Add(queued);
            _queue.Enqueue((runId, req));
            return queued;
        }

        _runs.Add(new AnalysisRun(
            runId, req.SessionId, req.From, req.To,
            AnalysisRun.ClaudeEngine, req.Model, AnalysisRun.Queued,
            Started: null, Finished: null, ReportJson: null, TokensUsed: 0));
        return Execute(runId, req);
    }

    /// <summary>Processes any queued runs once connectivity is restored (AC-DA5). No-op while offline.</summary>
    public IReadOnlyList<AnalysisRun> RunQueued()
    {
        var executed = new List<AnalysisRun>();
        if (!_connectivity.IsOnline)
            return executed;

        while (_queue.Count > 0)
        {
            var (runId, req) = _queue.Dequeue();
            executed.Add(Execute(runId, req));
        }

        return executed;
    }

    private AnalysisRun Execute(Guid runId, AnalysisRequest req)
    {
        var started = _clock.UtcNow;
        _runs.Update(new AnalysisRun(
            runId, req.SessionId, req.From, req.To,
            AnalysisRun.ClaudeEngine, req.Model, AnalysisRun.Running,
            started, Finished: null, ReportJson: null, TokensUsed: 0));

        var scopedSignals = _signals.GetSignals(int.MaxValue)
            .Where(s => (req.From is null || s.Time >= req.From) && (req.To is null || s.Time <= req.To))
            .ToList();
        var scopedEmitters = _emitters.All();

        // Deterministic tool layer + egress guard (AC-DA3): ONLY structured RF metadata leaves the boundary.
        var payload = SessionIntelligence.Assemble(req.SessionId, scopedSignals, scopedEmitters, [], []);
        EgressGuard.AssertNoRawIqOrContent(payload);

        long tokens = 0;
        var reportCitations = new List<EvidenceItem>();

        var candidates = scopedSignals.Where(IsCandidate).ToList();
        foreach (var s in candidates)
        {
            // Grounded in the ACTUAL signal record (AC-DA1, reclassification grounding).
            var citation = Cite("signal", s.Id.ToString(CultureInfo.InvariantCulture));
            var text = _claude.Complete(SystemPrompt, ReclassifyPrompt(s), [citation]);
            tokens += text.Length;
            reportCitations.Add(citation);

            var proposal = JsonSerializer.Serialize(new
            {
                originalProtocol = s.Protocol,
                originalConfidence = s.Confidence,
                rationale = text,
            });

            // Overlay-only (AC-DA2): we ADD an enrichment; we NEVER call any signal/emitter writer.
            _enrichments.Add(new Enrichment(
                Guid.NewGuid(), runId, "signal", s.Id.ToString(CultureInfo.InvariantCulture),
                Enrichment.Reclassification, proposal, [citation], Enrichment.Proposed, _clock.UtcNow));
        }

        // Grounded session report (AC-DA6): summary + the sources it cites.
        var report = JsonSerializer.Serialize(new
        {
            sessionId = req.SessionId,
            summary = $"Enhancement pass over {scopedSignals.Count} signal(s): "
                + $"{candidates.Count} residual candidate(s) reclassified as advisory overlays.",
            citations = reportCitations.Select(c => new { c.Feature, c.Value }).ToList(),
        });

        var done = new AnalysisRun(
            runId, req.SessionId, req.From, req.To,
            AnalysisRun.ClaudeEngine, req.Model, AnalysisRun.Done,
            started, _clock.UtcNow, report, tokens);
        _runs.Update(done);
        return done;
    }

    private static bool IsCandidate(Signal s) =>
        string.Equals(s.Protocol, "Unknown", StringComparison.OrdinalIgnoreCase)
        || s.Confidence < EnhancementCandidates.ConfidenceFloor;

    private static string ReclassifyPrompt(Signal s) =>
        $"Reclassify signal {s.Id}: protocol={s.Protocol}, confidence={s.Confidence:0.###}, "
        + $"center={s.CenterFreqHz} Hz, bw={s.BandwidthHz} Hz.";

    private static EvidenceItem Cite(string recordType, string id) => new(recordType, id, 1.0);
}
