using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

/// <summary>
/// Persistence performance + thread-safety hardening (#8, #10, #16): server-side reads over
/// full-table-scans, the new additive Count()/GetSince() query methods, and thread-safety/capacity
/// caps on the in-memory stores hit by concurrent live-pipeline writers and REST readers.
/// </summary>
public sealed class PerformanceHardeningTests
{
    private static Signal Sig(long id, DateTimeOffset time) => new(
        Id: id,
        Time: time,
        ObservationId: id,
        EmitterId: null,
        DeviceId: null,
        Protocol: "Unknown",
        Confidence: 0.5,
        Classifier: "test",
        Evidence: [new EvidenceItem("feature", "value", 1.0)],
        CenterFreqHz: 915_000_000,
        BandwidthHz: 1_000,
        DurationMs: 1,
        Features: new Dictionary<string, double>());

    // ── InMemoryAuditLog: thread-safety + capacity cap (#10) ────────────────────────────────────────

    [Fact]
    public void AuditLog_ConcurrentRecord_DoesNotThrow_AndProducesUniqueIds()
    {
        var log = new InMemoryAuditLog();

        Parallel.For(0, 1000, i => log.Record("actor", "action", $"query-{i}"));

        var recent = log.Recent(1000);
        Assert.Equal(1000, recent.Count);
        Assert.Equal(1000, recent.Select(e => e.Id).Distinct().Count()); // all ids unique
    }

    [Fact]
    public void AuditLog_Recent_IsBounded_ByMaxEntries_AndEvictsOldest()
    {
        var log = new InMemoryAuditLog();
        // Cross the cap for real (100_000 inserts is fast — well under a second) so eviction is
        // actually exercised, not just asserted-never-exceeded on a run that never reaches it.
        const int total = InMemoryAuditLog.MaxEntries + 10;
        for (var i = 1; i <= total; i++)
            log.Record("actor", "action", $"q{i}");

        var all = log.Recent(int.MaxValue);

        Assert.Equal(InMemoryAuditLog.MaxEntries, all.Count); // capped, not total
        // Oldest 10 (ids 1..10) were evicted; the smallest surviving id is 11.
        Assert.Equal(11, all[^1].Id);
        // Most-recent-first: the last recorded entry comes back first.
        Assert.Equal(total, all[0].Id);
        Assert.Equal($"q{total}", all[0].Query);
    }

    // ── Count()/GetSince() on the in-memory repos (#8) ──────────────────────────────────────────────

    [Fact]
    public void SignalRepository_Count_ReturnsCountAfterAdds()
    {
        var repo = new InMemorySignalRepository(seedDemo: true); // seeded with 1
        repo.Add(Sig(10, DateTimeOffset.UnixEpoch.AddSeconds(10)));
        repo.Add(Sig(20, DateTimeOffset.UnixEpoch.AddSeconds(20)));

        Assert.Equal(3, repo.Count());
    }

    [Fact]
    public void SignalRepository_GetSince_FiltersByTime_MostRecentFirst()
    {
        var repo = new InMemorySignalRepository();
        repo.ClearDemoSeed(); // start empty so assertions count only what we add
        var t0 = DateTimeOffset.UnixEpoch;
        repo.Add(Sig(10, t0.AddSeconds(10)));
        repo.Add(Sig(20, t0.AddSeconds(20)));
        repo.Add(Sig(30, t0.AddSeconds(30)));

        var since = repo.GetSince(t0.AddSeconds(15));

        Assert.Equal([30L, 20L], since.Select(s => s.Id));
    }

    [Fact]
    public void DeviceRepository_Count_ReturnsCountAfterUpserts()
    {
        var repo = new InMemoryDeviceRepository(new PassthroughResolver());
        repo.ClearDemoSeed();
        repo.Upsert(new Device("A", "t", null, new Dictionary<string, string>(), null, "p", 0.9,
            [new EvidenceItem("f", "v", 1.0)]));
        repo.Upsert(new Device("B", "t", null, new Dictionary<string, string>(), null, "p", 0.9,
            [new EvidenceItem("f", "v", 1.0)]));

        Assert.Equal(2, repo.Count());
    }

    [Fact]
    public void AlertRepository_Count_ReturnsCountAfterAdds()
    {
        var repo = new InMemoryAlertRepository(new NoopAnomalyEngine());
        var before = repo.Count(); // seeded alert(s)

        repo.Add(new Alert(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "e", "d", "kind", "info",
            "summary", [new EvidenceItem("f", "v", 1.0)]));

        Assert.Equal(before + 1, repo.Count());
    }

    [Fact]
    public void DeviceRepository_Get_ReturnsById_OrNull_WithoutFullScan()
    {
        var repo = new InMemoryDeviceRepository(new PassthroughResolver());
        repo.ClearDemoSeed();
        repo.Upsert(Dev("A"));
        repo.Upsert(Dev("B"));

        Assert.Equal("A", repo.Get("A")?.Id);
        Assert.Null(repo.Get("missing"));
    }

    [Fact]
    public void AlertRepository_GetSince_FiltersByTime_MostRecentFirst()
    {
        var repo = new InMemoryAlertRepository(new NoopAnomalyEngine()); // NoopAnomalyEngine → no seed
        var t0 = DateTimeOffset.UnixEpoch;
        repo.Add(Alrt("a10", t0.AddSeconds(10)));
        repo.Add(Alrt("a20", t0.AddSeconds(20)));
        repo.Add(Alrt("a30", t0.AddSeconds(30)));

        var since = repo.GetSince(t0.AddSeconds(15));

        Assert.Equal(["a30", "a20"], since.Select(a => a.Kind)); // windowed, newest-first
    }

    private static Device Dev(string id) => new(id, "t", null, new Dictionary<string, string>(),
        null, "p", 0.9, [new EvidenceItem("f", "v", 1.0)]);

    private static Alert Alrt(string kind, DateTimeOffset time) => new(
        Guid.NewGuid(), time, "e", "d", kind, "info", "summary", [new EvidenceItem("f", "v", 1.0)]);

    private sealed class PassthroughResolver : IDeviceResolver
    {
        public Device? Resolve(IReadOnlyList<DecodedFrame> frames) => null; // seed frame yields no device
    }

    private sealed class NoopAnomalyEngine : IAnomalyEngine
    {
        public IReadOnlyList<Alert> Evaluate(AnomalyEvent evt, EmitterBaseline baseline) => [];
    }

    // ── Enhancement repos: concurrency smoke test (#10) ─────────────────────────────────────────────

    [Fact]
    public void SessionRepository_ConcurrentAdds_DoNotThrow()
    {
        var repo = new InMemorySessionRepository();

        Parallel.For(0, 500, i => repo.Add(new Session($"S-{i}",
            DateTimeOffset.UnixEpoch, null, "{}", null, null)));

        Assert.Equal(500, repo.All().Count);
    }

    [Fact]
    public void AnalysisRunRepository_ConcurrentAdds_DoNotThrow()
    {
        var repo = new InMemoryAnalysisRunRepository();

        Parallel.For(0, 500, _ => repo.Add(new AnalysisRun(Guid.NewGuid(), null, null, null,
            "claude", "model", AnalysisRun.Queued, null, null, null, 0)));

        Assert.Equal(500, repo.All().Count);
    }

    [Fact]
    public void EnrichmentRepository_ConcurrentAdds_DoNotThrow()
    {
        var repo = new InMemoryEnrichmentRepository();
        var run = Guid.NewGuid();

        Parallel.For(0, 500, i => repo.Add(new Enrichment(Guid.NewGuid(), run, "signal", $"{i}",
            Enrichment.Reclassification, "{}", [new EvidenceItem("f", "v", 1.0)],
            Enrichment.Proposed, DateTimeOffset.UnixEpoch)));

        Assert.Equal(500, repo.Query(run, null).Count);
    }

    // ── Provider-split reads still work on SQLite (#8) ──────────────────────────────────────────────

    [Fact]
    public void EfSignalRepository_GetSignals_ReturnsNewestFirst_WithLimit_OnSqlite()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<SignalAtlasDbContext>().UseSqlite(connection).Options;
        using (var ctx = new SignalAtlasDbContext(options))
            ctx.Database.EnsureCreated();

        var t0 = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);
        using (var ctx = new SignalAtlasDbContext(options))
        {
            var repo = new EfSignalRepository(ctx);
            repo.Add(Sig(1, t0));
            repo.Add(Sig(2, t0.AddSeconds(1)));
            repo.Add(Sig(3, t0.AddSeconds(2)));
        }

        using var readCtx = new SignalAtlasDbContext(options);
        var read = new EfSignalRepository(readCtx).GetSignals(2);

        Assert.Equal(2, read.Count);
        Assert.Equal([3L, 2L], read.Select(s => s.Id)); // newest first, limited
    }

    [Fact]
    public void EfDeviceRepository_Get_ReturnsById_ViaKeyedFind_OnSqlite()
    {
        var options = NewSqliteDb(out var connection);
        using (connection)
        {
            using (var ctx = new SignalAtlasDbContext(options))
                new EfDeviceRepository(ctx).Upsert(Dev("DEV-1"));

            using var readCtx = new SignalAtlasDbContext(options);
            var repo = new EfDeviceRepository(readCtx);

            Assert.Equal("DEV-1", repo.Get("DEV-1")?.Id); // keyed Find, not a table scan
            Assert.Null(repo.Get("nope"));
        }
    }

    [Fact]
    public void EfAlertRepository_GetSince_FiltersByTime_MostRecentFirst_OnSqlite()
    {
        var options = NewSqliteDb(out var connection);
        using (connection)
        {
            var t0 = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);
            using (var ctx = new SignalAtlasDbContext(options))
            {
                var repo = new EfAlertRepository(ctx);
                repo.Add(Alrt("a10", t0.AddSeconds(10)));
                repo.Add(Alrt("a20", t0.AddSeconds(20)));
                repo.Add(Alrt("a30", t0.AddSeconds(30)));
            }

            using var readCtx = new SignalAtlasDbContext(options);
            var since = new EfAlertRepository(readCtx).GetSince(t0.AddSeconds(15));

            Assert.Equal(["a30", "a20"], since.Select(a => a.Kind)); // windowed, newest-first
        }
    }

    private static DbContextOptions<SignalAtlasDbContext> NewSqliteDb(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<SignalAtlasDbContext>().UseSqlite(connection).Options;
        using var ctx = new SignalAtlasDbContext(options);
        ctx.Database.EnsureCreated();
        return options;
    }
}
