using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// EF Core context for Signal Atlas identity/signal stores (SPEC §7.2). The SAME model runs on
/// SQLite (tests, offline) and PostgreSQL/TimescaleDB (prod): JSON-ish members (evidence lists,
/// identifier/feature dicts) are mapped through a System.Text.Json <see cref="ValueConverter"/>
/// to a plain <c>TEXT</c> column, so no provider-specific JSONB type is required (§4.3 portability).
///
/// KEY NOTE: SPEC §7.2 declares composite primary keys <c>(time, id)</c> on the hypertables
/// (observations/signals) — Timescale requires the partition column in every unique index. On
/// Npgsql this model therefore uses that composite PK, so <see cref="TimescaleInitializer"/> can turn
/// observations/signals into hypertables. On SQLite (tests/offline) there is no hypertable, so a
/// single-column surrogate key is kept: a shadow <c>Id</c> (identity) on <see cref="Observation"/>
/// (the record carries no Id) and the record's own <c>Id</c> on Signal (long). Device/Emitter (string),
/// Alert (Guid) and AuditEntry (identity long) are regular tables on both providers.
///
/// SCHEMA PATH: created with <c>EnsureCreated()</c> + <see cref="TimescaleInitializer"/>; forward-only
/// EF migrations are the remaining productionization step (dotnet-ef tooling was unavailable here).
/// </summary>
public sealed class SignalAtlasDbContext(DbContextOptions<SignalAtlasDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Emitter> Emitters => Set<Emitter>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    // Collect-now / analyze-later stores (SPEC §7.9): sessions, deferred Claude analysis runs, and the
    // additive, cited enrichment overlay. Present on both providers; only the enrichments CHECK differs.
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AnalysisRun> AnalysisRuns => Set<AnalysisRun>();
    public DbSet<Enrichment> Enrichments => Set<Enrichment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var evidenceConverter = JsonConverter<IReadOnlyList<EvidenceItem>, List<EvidenceItem>>();
        var evidenceComparer = JsonComparer<IReadOnlyList<EvidenceItem>, List<EvidenceItem>>();
        var featuresConverter = JsonConverter<IReadOnlyDictionary<string, double>, Dictionary<string, double>>();
        var featuresComparer = JsonComparer<IReadOnlyDictionary<string, double>, Dictionary<string, double>>();
        var idsConverter = JsonConverter<IReadOnlyDictionary<string, string>, Dictionary<string, string>>();
        var idsComparer = JsonComparer<IReadOnlyDictionary<string, string>, Dictionary<string, string>>();

        // HYPERTABLE NOTE (SPEC §7.2, §4.6): observations/signals are Timescale hypertables partitioned
        // by `time`. Timescale requires the partitioning column to be part of every unique index, so on
        // Npgsql the primary key is the SPEC-declared composite (time, id). On SQLite (tests/offline)
        // there is no hypertable, so the single-column surrogate key is kept — that also lets SQLite use
        // its INTEGER-PRIMARY-KEY autoincrement for the store-generated observation id. Snake-case
        // table/time names are pinned on both providers so the TimescaleInitializer SQL is literal.
        var isNpgsql = Database.IsNpgsql();

        b.Entity<Observation>(e =>
        {
            e.ToTable("observations");
            e.Property(o => o.Time).HasColumnName("time");
            // The Observation record carries no Id → shadow surrogate identity key.
            e.Property<long>("Id").ValueGeneratedOnAdd();
            if (isNpgsql)
                e.HasKey("Time", "Id");   // composite (time, id): partition column in the PK
            else
                e.HasKey("Id");           // surrogate key: single-column identity on SQLite
            e.Property(o => o.TimeSource).HasConversion<string>();
            e.Property(o => o.PowerRef).HasConversion<string>();
            e.Property(o => o.PositionQuality).HasConversion<string>();
        });

        b.Entity<Signal>(e =>
        {
            e.ToTable("signals");
            e.Property(s => s.Time).HasColumnName("time");
            e.Property(s => s.Id).ValueGeneratedNever();
            if (isNpgsql)
                e.HasKey("Time", "Id");   // composite (time, id): partition column in the PK
            else
                e.HasKey(s => s.Id);      // surrogate key on SQLite
            e.Property(s => s.Evidence).HasConversion(evidenceConverter, evidenceComparer);
            e.Property(s => s.Features).HasConversion(featuresConverter, featuresComparer);
        });

        b.Entity<Device>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Identifiers).HasConversion(idsConverter, idsComparer);
            e.Property(d => d.Evidence).HasConversion(evidenceConverter, evidenceComparer);
        });

        b.Entity<Emitter>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Identifiers).HasConversion(idsConverter, idsComparer);
            e.Property(m => m.Evidence).HasConversion(evidenceConverter, evidenceComparer);
        });

        b.Entity<Alert>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Evidence).HasConversion(evidenceConverter, evidenceComparer);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedOnAdd();
        });

        // SPEC §7.9 sessions: scan_plan / bbox are JSON stored as portable TEXT (§4.3).
        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Started).HasColumnName("started");
            e.Property(s => s.Ended).HasColumnName("ended");
            e.Property(s => s.ScanPlanJson).HasColumnName("scan_plan");
            e.Property(s => s.Bbox).HasColumnName("bbox");
            e.Property(s => s.Notes).HasColumnName("notes");
        });

        // SPEC §7.9 analysis_runs: a deferred Claude pass; status queued|running|done|failed.
        b.Entity<AnalysisRun>(e =>
        {
            e.ToTable("analysis_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.SessionId).HasColumnName("session_id");
            e.Property(r => r.From).HasColumnName("from_time");
            e.Property(r => r.To).HasColumnName("to_time");
            e.Property(r => r.ReportJson).HasColumnName("report");
            e.Property(r => r.TokensUsed).HasColumnName("tokens_used");
        });

        // SPEC §7.9 enrichments: additive, attributed, advisory overlay. Citations map to portable TEXT
        // JSON. NON-EMPTY CITATIONS (AC-DA1) is enforced at the app layer in the repository Add (SQLite
        // can't CHECK jsonb_array_length on JSON-as-text). On Npgsql we ALSO add a belt-and-suspenders
        // CHECK approximating enrichment_cited — a non-empty JSON array can't be the literal '[]'.
        b.Entity<Enrichment>(e =>
        {
            e.ToTable("enrichments", t =>
            {
                if (isNpgsql)
                    t.HasCheckConstraint("enrichment_cited", "citations IS NOT NULL AND citations <> '[]'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.TargetEntity).HasColumnName("target_entity");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.ProposalJson).HasColumnName("proposal");
            e.Property(x => x.Citations).HasColumnName("citations").HasConversion(evidenceConverter, evidenceComparer);
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Created).HasColumnName("created");
        });
    }

    // TModel is the (read-only) member type; TConcrete is the concrete type STJ round-trips through.
    // We serialize the interface type directly (never cast to TConcrete — the seed uses collection
    // expressions that yield arrays, which are IReadOnlyList but not List).
    private static ValueConverter<TModel, string> JsonConverter<TModel, TConcrete>()
        where TConcrete : TModel, new()
        => new(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<TConcrete>(v, Json) ?? new TConcrete());

    private static ValueComparer<TModel> JsonComparer<TModel, TConcrete>()
        where TConcrete : TModel, new()
        => new(
            (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
            v => JsonSerializer.Serialize(v, Json).GetHashCode(),
            v => JsonSerializer.Deserialize<TConcrete>(JsonSerializer.Serialize(v, Json), Json)!);
}
