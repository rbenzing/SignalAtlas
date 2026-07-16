using Microsoft.EntityFrameworkCore;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;
using Testcontainers.PostgreSql;

namespace SignalAtlas.Tests.Integration;

/// <summary>
/// M0-T6 / M0-T7 — PostgreSQL+TimescaleDB round-trip and encryption-at-rest (NFR-S1).
/// The whole class is <c>[Trait("Category","NeedsDocker")]</c>, so the default local test run
/// (<c>--filter "Category!=NeedsDocker"</c>) skips it entirely and never touches a Docker host.
/// A Docker CI lane runs it to prove the SAME <see cref="SignalAtlasDbContext"/> that backs the
/// SQLite round-trip tests also round-trips on real Timescale.
/// </summary>
[Trait("Category", "NeedsDocker")]
public class PersistenceTests
{
    private const string TimescaleImage = "timescale/timescaledb:latest-pg16";

    private const string NativeTdeUnavailable =
        "Native at-rest encryption (TDE) is unavailable in upstream PostgreSQL (SPEC §4.6, §18): " +
        "the baseline is filesystem/LUKS volume encryption, an ops/deploy concern not exercisable " +
        "from a plain Testcontainers image. Kept as an executable spec of the §12.4 gate intent.";

    // M0-T6 — persist an observation on real TimescaleDB and read it back unchanged.
    [Fact]
    public async Task Observation_RoundTrips_OnTimescale()
    {
        await using var postgres = new PostgreSqlBuilder(TimescaleImage)
            .Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<SignalAtlasDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;

        await using (var ctx = new SignalAtlasDbContext(options))
            await ctx.Database.EnsureCreatedAsync();

        var obs = new Observation(
            Time: new DateTimeOffset(2026, 7, 7, 12, 34, 56, TimeSpan.Zero),
            TimeSource: TimeSource.Gps,
            CollectorId: "col-1",
            Seq: 42,
            FrequencyHz: 915_000_000,
            BandwidthHz: 125_000,
            Power: -37.5,
            PowerRef: PowerRef.Relative,
            SnrDb: 11.25,
            Latitude: 42.36,
            Longitude: -71.06,
            PositionQuality: PositionQuality.Good,
            IqRef: "ring://0/42",
            CorrelationId: Guid.Parse("11111111-2222-3333-4444-555555555555"));

        await using (var write = new SignalAtlasDbContext(options))
            new EfObservationRepository(write).Add(obs);

        await using var read = new SignalAtlasDbContext(options);
        var got = Assert.Single(new EfObservationRepository(read).GetRecent(10));

        Assert.Equal(obs.Time, got.Time);
        Assert.Equal(obs.Power, got.Power);
        Assert.Equal(obs.PowerRef, got.PowerRef);
        Assert.Equal(obs.PositionQuality, got.PositionQuality);
        Assert.Equal(obs.CorrelationId, got.CorrelationId);
    }

    // M0-T7 — a cold copy of the data files reveals no plaintext identifiers (NFR-S1).
    [Fact(Skip = NativeTdeUnavailable)]
    public void ColdFileCopy_RevealsNoPlaintextIdentifiers()
    {
        // Arrange: write device rows containing a known identifier (e.g. a BSSID) to an encrypted volume.
        // Act:     copy the raw data files and scan their bytes for the identifier.
        // Assert:  the identifier does not appear in plaintext.
        Assert.Fail("volume-encryption verification is an ops concern — see skip reason");
    }
}
