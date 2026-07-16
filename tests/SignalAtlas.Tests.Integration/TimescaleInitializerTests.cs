using Microsoft.EntityFrameworkCore;
using SignalAtlas.Persistence;
using Testcontainers.PostgreSql;

namespace SignalAtlas.Tests.Integration;

/// <summary>
/// M0 productionization — the <see cref="TimescaleInitializer"/> turns <c>observations</c> into a
/// TimescaleDB hypertable (SPEC §4.6, §7.6, §18.4). The whole class is
/// <c>[Trait("Category","NeedsDocker")]</c>, so the default local run (<c>--filter
/// "Category!=NeedsDocker"</c>) skips it and never touches Docker; a Docker CI lane runs it against
/// a real <c>timescale/timescaledb:latest-pg16</c> node.
/// </summary>
[Trait("Category", "NeedsDocker")]
public class TimescaleInitializerTests
{
    private const string TimescaleImage = "timescale/timescaledb:latest-pg16";

    [Fact]
    public async Task Initialize_MakesObservations_AHypertable()
    {
        await using var postgres = new PostgreSqlBuilder(TimescaleImage).Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<SignalAtlasDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;

        await using var ctx = new SignalAtlasDbContext(options);
        await ctx.Database.EnsureCreatedAsync();

        // Runs without error and is idempotent (a second call is a no-op via if_not_exists).
        TimescaleInitializer.Initialize(ctx);
        TimescaleInitializer.Initialize(ctx);

        var count = await ctx.Database
            .SqlQuery<int>(
                $"SELECT COUNT(*)::int AS \"Value\" FROM timescaledb_information.hypertables WHERE hypertable_name = 'observations'")
            .SingleAsync();

        Assert.Equal(1, count);
    }
}
