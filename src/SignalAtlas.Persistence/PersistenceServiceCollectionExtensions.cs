using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration; // GetValue<T> extension (Microsoft.Extensions.Configuration.Binder package)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SignalAtlas.Domain;

namespace SignalAtlas.Persistence;

/// <summary>
/// Docker-OPTIONAL persistence wiring (SPEC §4.3). If a "SignalAtlas" connection string is
/// configured → PostgreSQL/TimescaleDB via EF Core (schema by <c>EnsureCreated</c>, seeded at
/// startup). Otherwise → the offline-first in-memory seeded repos, no database required.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddSignalAtlasPersistence(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("SignalAtlas");
        // Demo-data seeding is OFF by default (product decision: an offline install should show
        // honest empty state, not fabricated demo contacts). Set SeedDemoData=true to restore the
        // old seeded-offline-demo behavior (in-memory mode and the DB-empty seed both honor it).
        var seedDemo = config.GetValue<bool>("SeedDemoData", false);

        // Spectrum waterfall buffer (SPEC §8.2): always in-memory (there is no PSD-frame DB table);
        // optionally seeded with synthetic frames so the Spectrum views render offline (§4.3, gated by
        // SeedDemoData). The live pipeline pushes real frames on top of any seeds (bounded ring, NFR-C3).
        services.AddSingleton<InMemorySpectrumBuffer>(_ =>
        {
            var buffer = new InMemorySpectrumBuffer();
            if (seedDemo)
                foreach (var frame in SpectrumSeed.Frames())
                    buffer.Push(frame);
            return buffer;
        });
        services.AddSingleton<ISpectrumBuffer>(sp => sp.GetRequiredService<InMemorySpectrumBuffer>());
        // The buffer's seeded frames are demo data: cleared when a real device starts streaming.
        services.AddSingleton<IDemoSeedStore>(sp => sp.GetRequiredService<InMemorySpectrumBuffer>());
        // Transient RF store: cleared on retune (a new center frequency) so a new band starts clean.
        services.AddSingleton<ITransientStore>(sp => sp.GetRequiredService<InMemorySpectrumBuffer>());

        // Live-session coordinator (always registered): the first WebUSB stream clears every
        // IDemoSeedStore once, so the UI shows live data only. In DB mode the only IDemoSeedStore is
        // the spectrum buffer (EF repos don't implement it), so a real database is never touched.
        services.AddSingleton<ILiveSession, LiveSession>();

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<SignalAtlasDbContext>(opt => opt.UseNpgsql(connectionString));

            services.AddScoped<IObservationRepository, EfObservationRepository>();
            // Signal read + write share one instance: register the concrete once, forward both interfaces.
            services.AddScoped<EfSignalRepository>();
            services.AddScoped<ISignalRepository>(sp => sp.GetRequiredService<EfSignalRepository>());
            services.AddScoped<ISignalWriter>(sp => sp.GetRequiredService<EfSignalRepository>());
            services.AddScoped<IDeviceRepository, EfDeviceRepository>();
            services.AddScoped<IEmitterRepository, EfEmitterRepository>();
            // Alert read + write share one instance: register the concrete once, forward both interfaces.
            services.AddScoped<EfAlertRepository>();
            services.AddScoped<IAlertRepository>(sp => sp.GetRequiredService<EfAlertRepository>());
            services.AddScoped<IAlertWriter>(sp => sp.GetRequiredService<EfAlertRepository>());
            services.AddScoped<IAuditLog, EfAuditLog>();
            // Collect-now / analyze-later stores (SPEC §7.9, M13).
            services.AddScoped<ISessionRepository, EfSessionRepository>();
            services.AddScoped<IAnalysisRunRepository, EfAnalysisRunRepository>();
            services.AddScoped<IEnrichmentRepository, EfEnrichmentRepository>();

            // EnsureCreated + Timescale hypertables/retention + seed-if-empty at host start (scoped context).
            services.AddHostedService<DatabaseInitializer>();
        }
        else
        {
            services.AddSingleton<IObservationRepository, InMemoryObservationRepository>();
            // Signal read + write share one instance: register the concrete once, forward both interfaces.
            services.AddSingleton(sp => new InMemorySignalRepository(seedDemo));
            services.AddSingleton<ISignalRepository>(sp => sp.GetRequiredService<InMemorySignalRepository>());
            services.AddSingleton<ISignalWriter>(sp => sp.GetRequiredService<InMemorySignalRepository>());
            services.AddSingleton<IDemoSeedStore>(sp => sp.GetRequiredService<InMemorySignalRepository>());
            services.AddSingleton<ITransientStore>(sp => sp.GetRequiredService<InMemorySignalRepository>());
            services.AddSingleton(sp => new InMemoryDeviceRepository(sp.GetRequiredService<IDeviceResolver>(), seedDemo));
            services.AddSingleton<IDeviceRepository>(sp => sp.GetRequiredService<InMemoryDeviceRepository>());
            services.AddSingleton<IDemoSeedStore>(sp => sp.GetRequiredService<InMemoryDeviceRepository>());
            services.AddSingleton(sp => new InMemoryEmitterRepository(seedDemo));
            services.AddSingleton<IEmitterRepository>(sp => sp.GetRequiredService<InMemoryEmitterRepository>());
            services.AddSingleton<IDemoSeedStore>(sp => sp.GetRequiredService<InMemoryEmitterRepository>());
            services.AddSingleton<ITransientStore>(sp => sp.GetRequiredService<InMemoryEmitterRepository>());
            // Alert read + write share one instance: register the concrete once, forward both interfaces.
            services.AddSingleton(sp => new InMemoryAlertRepository(sp.GetRequiredService<IAnomalyEngine>(), seedDemo));
            services.AddSingleton<IAlertRepository>(sp => sp.GetRequiredService<InMemoryAlertRepository>());
            services.AddSingleton<IAlertWriter>(sp => sp.GetRequiredService<InMemoryAlertRepository>());
            services.AddSingleton<IDemoSeedStore>(sp => sp.GetRequiredService<InMemoryAlertRepository>());
            services.AddSingleton<ITransientStore>(sp => sp.GetRequiredService<InMemoryAlertRepository>());
            services.AddSingleton<IAuditLog, InMemoryAuditLog>();
            // Collect-now / analyze-later stores (SPEC §7.9, M13): offline-first in-memory.
            services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
            services.AddSingleton<IAnalysisRunRepository, InMemoryAnalysisRunRepository>();
            services.AddSingleton<IEnrichmentRepository, InMemoryEnrichmentRepository>();
        }

        return services;
    }
}

/// <summary>
/// Applies the forward-only EF migrations, the Timescale hypertables/retention, then seeds demo
/// rows at host startup (Npgsql path only). Migrations own the schema in production; the SQLite
/// round-trip/integration tests build the SAME model directly via <c>EnsureCreated</c>.
/// </summary>
internal sealed class DatabaseInitializer(IServiceProvider services, IConfiguration config) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SignalAtlasDbContext>();

        db.Database.Migrate();                  // forward-only Npgsql schema (composite time-PK hypertables).
        TimescaleInitializer.Initialize(db);    // Postgres-only hypertables + retention; no-op on SQLite.

        // Demo-data seeding is OFF by default (SeedDemoData config, default false) — a fresh DB stays
        // empty unless a developer opts in.
        if (config.GetValue<bool>("SeedDemoData", false))
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IDeviceResolver>();
            var engine = scope.ServiceProvider.GetRequiredService<IAnomalyEngine>();
            DatabaseSeeder.SeedIfEmpty(db, resolver, engine, seedDemo: true);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
