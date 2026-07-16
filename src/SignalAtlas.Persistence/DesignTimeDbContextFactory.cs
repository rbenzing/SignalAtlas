using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SignalAtlas.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the Npgsql model without a running
/// host (SPEC §7.2). The connection string is a placeholder — migrations only need the provider to
/// resolve the correct (composite-PK) model, not a live database.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SignalAtlasDbContext>
{
    public SignalAtlasDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SignalAtlasDbContext>()
            .UseNpgsql("Host=localhost;Database=signalatlas;Username=postgres;Password=postgres")
            .Options;
        return new SignalAtlasDbContext(options);
    }
}
