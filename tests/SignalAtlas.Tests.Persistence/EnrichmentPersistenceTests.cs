using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;

namespace SignalAtlas.Tests.Persistence;

/// <summary>
/// M13 (SPEC §7.9, §8.13) persistence: the non-empty-citation guard (AC-DA1) enforced by BOTH repo
/// impls, and the accept/reject lifecycle round-tripping through the SAME DbContext that runs on
/// Postgres in prod (AC-DA4), exercised here on SQLite.
/// </summary>
public sealed class EnrichmentPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SignalAtlasDbContext> _options;

    public EnrichmentPersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SignalAtlasDbContext>().UseSqlite(_connection).Options;
        using var ctx = new SignalAtlasDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    private SignalAtlasDbContext NewContext() => new(_options);

    private static Enrichment Cited(string status = Enrichment.Proposed) => new(
        Guid.NewGuid(), Guid.NewGuid(), "signal", "42", Enrichment.Reclassification,
        "{\"rationale\":\"likely LoRa\"}",
        [new EvidenceItem("signal", "42", 1.0)],
        status, new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero));

    private static Enrichment Uncited() => Cited() with { Citations = [] };

    // ── AC-DA1: empty-citation enrichment rejected by the repository guard ──────────────────────────

    [Fact]
    public void InMemoryRepository_Rejects_EmptyCitations()
        => Assert.Throws<InvalidOperationException>(() => new InMemoryEnrichmentRepository().Add(Uncited()));

    [Fact]
    public void EfRepository_Rejects_EmptyCitations()
    {
        var repo = new EfEnrichmentRepository(NewContext());
        Assert.Throws<InvalidOperationException>(() => repo.Add(Uncited()));
    }

    [Fact]
    public void Cited_Enrichment_RoundTrips_WithCitationsPreserved()
    {
        var e = Cited();
        new EfEnrichmentRepository(NewContext()).Add(e);

        var read = new EfEnrichmentRepository(NewContext()).Get(e.Id);

        Assert.NotNull(read);
        Assert.Single(read!.Citations);
        Assert.Equal("42", read.Citations[0].Value);
        Assert.Equal(Enrichment.Proposed, read.Status);
    }

    // ── AC-DA4: accept/reject lifecycle persists ────────────────────────────────────────────────────

    [Theory]
    [InlineData(Enrichment.Accepted)]
    [InlineData(Enrichment.Rejected)]
    public void SetStatus_Persists_AcceptOrReject(string status)
    {
        var e = Cited();
        new EfEnrichmentRepository(NewContext()).Add(e);

        new EfEnrichmentRepository(NewContext()).SetStatus(e.Id, status);

        Assert.Equal(status, new EfEnrichmentRepository(NewContext()).Get(e.Id)!.Status);
    }

    [Fact]
    public void Query_FiltersByRunAndTarget()
    {
        var run = Guid.NewGuid();
        var a = Cited() with { Id = Guid.NewGuid(), RunId = run, TargetId = "42" };
        var b = Cited() with { Id = Guid.NewGuid(), RunId = run, TargetId = "99" };
        var repo = new EfEnrichmentRepository(NewContext());
        repo.Add(a);
        repo.Add(b);

        Assert.Equal(2, new EfEnrichmentRepository(NewContext()).Query(run, null).Count);
        Assert.Single(new EfEnrichmentRepository(NewContext()).Query(run, "42"));
    }

    public void Dispose() => _connection.Dispose();
}
