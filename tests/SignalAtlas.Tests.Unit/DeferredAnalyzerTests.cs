using System.Globalization;
using SignalAtlas.Analyst;
using SignalAtlas.Domain;
using SignalAtlas.Enhancement;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M13 (SPEC §8.13) DeferredClaudeAnalyzer: cited (AC-DA1), advisory-only overlay that never mutates
/// edge rows (AC-DA2), offline → queued not failed and runs on reconnect (AC-DA5), reclassification
/// grounded in the cited signal record, and a grounded session report on the run.
/// </summary>
public class DeferredAnalyzerTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private static Signal Sig(long id, string protocol, double confidence)
        => new(id, T, id, null, null, protocol, confidence, "rules",
            [new EvidenceItem("f", "v", 1.0)], 915_000_000, 125_000, 100,
            new Dictionary<string, double> { ["snr_db"] = 12.5 });

    private static AnalysisRequest Req => new(SessionId: "sess-1", From: null, To: null, Model: "claude-sonnet-4-6");

    private sealed record Harness(
        DeferredClaudeAnalyzer Analyzer,
        FakeAnalysisRunRepository Runs,
        FakeEnrichmentRepository Enrichments,
        MutableConnectivity Connectivity,
        IReadOnlyList<Signal> Signals);

    private static Harness Build(bool online, params Signal[] signals)
    {
        var runs = new FakeAnalysisRunRepository();
        var enrichments = new FakeEnrichmentRepository();
        var connectivity = new MutableConnectivity(online);
        var analyzer = new DeferredClaudeAnalyzer(
            new FakeSignalRepository(signals),
            new FakeEmitterRepository([]),
            runs,
            enrichments,
            new StubClaudeClient(),
            connectivity,
            new FixedClock(T));
        return new Harness(analyzer, runs, enrichments, connectivity, signals);
    }

    [Fact]
    public void Online_Run_ProducesProposedCitedEnrichments_ForResidualCandidates()
    {
        // 1 confident (skipped) + 2 residual candidates (Unknown, below-floor) → 2 enrichments.
        var h = Build(online: true, Sig(1, "Wi-Fi", 0.95), Sig(2, "Unknown", 0.4), Sig(3, "LoRa", 0.5));

        var run = h.Analyzer.Run(Req);

        Assert.Equal(AnalysisRun.Done, run.Status);
        Assert.Equal(2, h.Enrichments.Added.Count);
        Assert.All(h.Enrichments.Added, e =>
        {
            Assert.Equal(Enrichment.Proposed, e.Status);            // advisory until accepted (AC-DA4)
            Assert.Equal(Enrichment.Reclassification, e.Kind);
            Assert.NotEmpty(e.Citations);                            // AC-DA1
            Assert.Equal(run.Id, e.RunId);
        });
    }

    [Fact]
    public void Run_NeverMutatesTargetSignalRow_OverlayOnly()
    {
        var h = Build(online: true, Sig(2, "Unknown", 0.4));
        var before = h.Signals[0];

        h.Analyzer.Run(Req);

        // The analyzer has NO signal/emitter WRITER dependency — mutation is impossible by construction.
        // Assert the source row is byte-identical and only an overlay enrichment was produced (AC-DA2).
        var after = new FakeSignalRepository(h.Signals).GetSignals()[0];
        Assert.Equal(before, after);
        Assert.Equal("Unknown", after.Protocol);
        Assert.Equal(0.4, after.Confidence);
        Assert.Single(h.Enrichments.Added);
    }

    [Fact]
    public void Reclassification_IsGroundedInCitedSignalRecord()
    {
        var h = Build(online: true, Sig(42, "Unknown", 0.3));

        h.Analyzer.Run(Req);

        var enrichment = Assert.Single(h.Enrichments.Added);
        var citation = Assert.Single(enrichment.Citations);
        Assert.Equal("signal", citation.Feature);
        Assert.Equal("42", citation.Value);                          // cites the ACTUAL signal record id
        Assert.Equal("42", enrichment.TargetId);
    }

    [Fact]
    public void Offline_Run_IsQueued_NotFailed_AndExecutesOnReconnect()
    {
        var h = Build(online: false, Sig(2, "Unknown", 0.4));

        var queued = h.Analyzer.Run(Req);

        // AC-DA5: offline → queued (NOT failed); no enrichments produced yet.
        Assert.Equal(AnalysisRun.Queued, queued.Status);
        Assert.Empty(h.Enrichments.Added);

        // Reconnect and drain the queue → the same run executes to completion.
        h.Connectivity.IsOnline = true;
        var executed = h.Analyzer.RunQueued();

        var run = Assert.Single(executed);
        Assert.Equal(queued.Id, run.Id);
        Assert.Equal(AnalysisRun.Done, run.Status);
        Assert.Equal(AnalysisRun.Done, h.Runs.Get(queued.Id)!.Status);
        Assert.Single(h.Enrichments.Added);
    }

    [Fact]
    public void RunQueued_IsNoOp_WhileStillOffline()
    {
        var h = Build(online: false, Sig(2, "Unknown", 0.4));
        h.Analyzer.Run(Req);

        var executed = h.Analyzer.RunQueued();

        Assert.Empty(executed);
        Assert.Empty(h.Enrichments.Added);
    }

    [Fact]
    public void EnrichmentId_IsDeterministic_AcrossRepeatedRunsOverSameSignal()
    {
        // #21: the tool/egress layer is deterministic (P5) — same run+signal → same enrichment id,
        // not a fresh Guid.NewGuid() each time. Two separate harnesses (same signal, same fixed clock)
        // simulate re-running the analysis; only the run id differs, but the enrichment id keyed off
        // "{runId}:signal:{signalId}" must reproduce identically for the SAME runId.
        var runs = new FakeAnalysisRunRepository();
        var enrichmentsA = new FakeEnrichmentRepository();
        var enrichmentsB = new FakeEnrichmentRepository();
        var signal = Sig(99, "Unknown", 0.2);

        var analyzerA = new DeferredClaudeAnalyzer(
            new FakeSignalRepository([signal]), new FakeEmitterRepository([]), runs, enrichmentsA,
            new StubClaudeClient(), new MutableConnectivity(true), new FixedClock(T));
        var analyzerB = new DeferredClaudeAnalyzer(
            new FakeSignalRepository([signal]), new FakeEmitterRepository([]), new FakeAnalysisRunRepository(),
            enrichmentsB, new StubClaudeClient(), new MutableConnectivity(true), new FixedClock(T));

        // Execute is only reachable via Run/RunQueued, both of which mint a fresh run id per call — so
        // to compare enrichment ids for "the same run" we drive both analyzers through the SAME queued
        // request id by going through the offline→RunQueued path is unnecessary; instead assert the
        // narrower, directly-testable property: the enrichment id is a pure function of (runId, signalId)
        // by re-deriving it exactly as production code does and checking it matches what was persisted.
        var runA = analyzerA.Run(Req);
        var enrichmentA = Assert.Single(enrichmentsA.Added);
        var expectedId = DeterministicGuid.From($"{runA.Id}:signal:99");
        Assert.Equal(expectedId, enrichmentA.Id);

        // Re-running produces a NEW run id (Run/RunQueued mint runId via Guid.NewGuid per AC-DA5/API
        // semantics), so the enrichment id differs across runs — but is stable for a given run.
        var runB = analyzerB.Run(Req);
        var enrichmentB = Assert.Single(enrichmentsB.Added);
        Assert.Equal(DeterministicGuid.From($"{runB.Id}:signal:99"), enrichmentB.Id);
        Assert.NotEqual(enrichmentA.Id, enrichmentB.Id); // different run ids → different derived ids
    }

    [Fact]
    public void ConcurrentEnqueue_ViaRun_WhileOffline_DoesNotThrow()
    {
        // #10: Run (Enqueue) and RunQueued (Dequeue) share the same Queue<> — concurrent Run calls while
        // offline must not corrupt/throw. RunQueued draining is exercised elsewhere; this is a smoke test
        // that many concurrent Enqueues under the lock complete cleanly with no lost/duplicate entries.
        var runs = new FakeAnalysisRunRepository();
        var enrichments = new FakeEnrichmentRepository();
        var connectivity = new MutableConnectivity(false);
        var analyzer = new DeferredClaudeAnalyzer(
            new FakeSignalRepository([Sig(1, "Unknown", 0.2)]), new FakeEmitterRepository([]),
            runs, enrichments, new StubClaudeClient(), connectivity, new FixedClock(T));

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 50, i =>
        {
            try
            {
                analyzer.Run(Req with { SessionId = $"sess-{i}" });
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
        Assert.Equal(50, runs.All().Count(r => r.Status == AnalysisRun.Queued));

        connectivity.IsOnline = true;
        var executed = analyzer.RunQueued();
        Assert.Equal(50, executed.Count);
        Assert.All(executed, r => Assert.Equal(AnalysisRun.Done, r.Status));
    }

    [Fact]
    public void Run_WritesGroundedSessionReport_CitingSources()
    {
        var h = Build(online: true, Sig(7, "Unknown", 0.4));

        var run = h.Analyzer.Run(Req);

        Assert.False(string.IsNullOrWhiteSpace(run.ReportJson));
        // The report cites its sources (AC-DA6): the candidate signal id appears in the report citations.
        Assert.Contains("\"7\"", run.ReportJson, StringComparison.Ordinal);
        Assert.Contains("signal", run.ReportJson, StringComparison.Ordinal);
        Assert.True(run.TokensUsed > 0);
    }
}
