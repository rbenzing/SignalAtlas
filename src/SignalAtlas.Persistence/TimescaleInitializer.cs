using Microsoft.EntityFrameworkCore;

namespace SignalAtlas.Persistence;

/// <summary>
/// Turns the time-series tables into TimescaleDB hypertables and applies tiered retention
/// (SPEC §4.6, §7.6, §18.4) — Postgres-only, idempotent, and a NO-OP on any other provider
/// (SQLite tests/offline). Runs AFTER <c>EnsureCreated</c> on empty tables.
///
/// HYPERTABLE + SURROGATE-KEY DECISION (SPEC §7.2): Timescale requires the partition column to be
/// part of every unique index, so a plain surrogate <c>id</c> PK cannot be a hypertable. The
/// <see cref="SignalAtlasDbContext"/> therefore uses the SPEC-declared composite PK <c>(time, id)</c>
/// on Npgsql for observations/signals, which makes <c>create_hypertable('...', 'time')</c> succeed.
///
/// RETENTION (§7.6): raw observations dropped after 7d; raw signals after 30d. Rollups/continuous
/// aggregates keep longer (retention does NOT cascade to them — §18.4 caveat) and are a follow-up.
/// </summary>
public static class TimescaleInitializer
{
    private const string Sql = """
        CREATE EXTENSION IF NOT EXISTS timescaledb;
        SELECT create_hypertable('observations', 'time', if_not_exists => TRUE, migrate_data => TRUE);
        SELECT create_hypertable('signals', 'time', if_not_exists => TRUE, migrate_data => TRUE);
        SELECT add_retention_policy('observations', INTERVAL '7 days', if_not_exists => TRUE);
        SELECT add_retention_policy('signals', INTERVAL '30 days', if_not_exists => TRUE);
        """;

    /// <summary>Runs the idempotent Timescale setup; no-op unless the context is on Npgsql.</summary>
    public static void Initialize(SignalAtlasDbContext db)
    {
        if (!db.Database.IsNpgsql())
            return; // SQLite / other providers: hypertables + retention do not apply.

        db.Database.ExecuteSqlRaw(Sql);
    }
}
